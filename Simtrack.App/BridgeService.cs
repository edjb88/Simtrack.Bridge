using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Simtrack.Communication;
using Simtrack.Core.Models;
using Simtrack.Core.Tracking;
using Simtrack.Output;

namespace Simtrack.App;

/// <summary>
/// Une todas las piezas: recibe del iPhone, normaliza la pose, escribe en
/// FreeTrack 2.0 a 250 Hz y devuelve telemetría al iPhone.
///
/// Sin fusionar todavía con los Sony: la rotación y la posición vienen del
/// iPhone (TrueDepth), que es la primera fase prevista.
/// </summary>
public sealed class BridgeService : IDisposable
{
    private readonly object _gate = new();

    private readonly IphoneReceiver _receiver = new();
    private readonly PoseFusion _fusion = new();
    private readonly FreeTrackWriter _writer = new();
    private readonly TelemetrySender _telemetry = new();

    private readonly SensorStatus _headphones = new()
    {
        Connected = false,
        DeviceName = "Sony ULT WEAR (sin integrar)",
        ImuAvailable = false,
        SampleRateHz = 0
    };

    private readonly SensorStatus _iphone = new()
    {
        Connected = false,
        DeviceName = "iPhone X / TrueDepth",
        SampleRateHz = 0
    };

    private CancellationTokenSource? _cancellation;
    private Task? _loopTask;

    private long _writeCounter;
    private double _writeRateHz;
    private DateTime _rateWindowStart = DateTime.UtcNow;
    private long _rateWindowCount;

    private DateTime _lastTelemetry = DateTime.MinValue;
    private IPAddress? _lastAddress;

    private const string SettingsFileName = "simtrack.settings.json";

    public BridgeService()
    {
        LoadSettings();
    }

    public event Action<string>? LogReceived;

    /// <summary>Avisa de una orden ejecutada ("Windows" o "iPhone") para la interfaz.</summary>
    public event Action<string>? CommandReceived;

    public DateTime StartedAt { get; private set; }

    public PoseFusion Fusion => _fusion;
    public bool IsRunning => _loopTask is { IsCompleted: false };
    public bool FreeTrackOpen => _writer.IsOpen;

    public long PacketsReceived => _receiver.PacketsReceived;
    public long PacketsRejected => _receiver.PacketsRejected;
    public double WriteRateHz { get { lock (_gate) { return _writeRateHz; } } }
    public IPAddress? RemoteAddress => _lastAddress;
    public PoseSample CurrentPose => _fusion.CurrentPose;
    public bool IphoneFresh => _fusion.HasFreshPose;
    public DateTime LastWriteAt => _writer.LastWriteAt;

    public void Start()
    {
        if (IsRunning)
            return;

        _receiver.LogReceived += OnReceiverLog;
        _receiver.PoseReceived += OnPoseReceived;
        _receiver.RecenterRequested += OnRecenterRequested;

        try
        {
            _writer.Open();
            Log($"Memoria compartida {FreeTrackLayout.SharedMemoryName} abierta " +
                $"({FreeTrackLayout.HeapSize} bytes, FTData {FreeTrackLayout.DataSize} bytes).");

            _receiver.Start();
        }
        catch (Exception ex)
        {
            Log($"ERROR al arrancar: {ex.Message}");

            _writer.Close();
            DetachReceiver();

            throw;
        }

        _cancellation = new CancellationTokenSource();
        _loopTask = Task.Run(() => WriteLoop(_cancellation.Token));

        StartedAt = DateTime.UtcNow;

        Log("Motor de salida iniciado (objetivo 250 Hz).");
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation = _cancellation;
        Task? loop = _loopTask;

        _cancellation = null;
        _loopTask = null;

        if (cancellation is null && loop is null)
            return;

        cancellation?.Cancel();

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        cancellation?.Dispose();

        DetachReceiver();
        _receiver.Stop();

        _writer.Close();

        Log("Motor de salida detenido.");
    }

    public void Recenter(string origin = "Windows")
    {
        _fusion.Recenter();

        PoseSample pose = _fusion.CurrentPose;
        CommandReceived?.Invoke($"Recentrado desde {origin} · {DateTime.Now:HH:mm:ss}");
        Log($"Recentrado ({origin}): " +
            $"X {pose.X:F1} cm, Y {pose.Y:F1} cm, Z {pose.Z:F1} cm, " +
            $"Yaw {pose.Yaw * 180 / Math.PI:F1}°, " +
            $"Pitch {pose.Pitch * 180 / Math.PI:F1}°, " +
            $"Roll {pose.Roll * 180 / Math.PI:F1}°.");
    }

    private void DetachReceiver()
    {
        _receiver.LogReceived -= OnReceiverLog;
        _receiver.PoseReceived -= OnPoseReceived;
        _receiver.RecenterRequested -= OnRecenterRequested;
    }

    private void OnReceiverLog(string message)
    {
        Log(message);
    }

    private void OnPoseReceived(HeadPose pose, IPAddress address)
    {
        _fusion.ApplyIphonePose(pose);

        bool isNewAddress;

        lock (_gate)
        {
            isNewAddress = _lastAddress is null || !_lastAddress.Equals(address);
            _lastAddress = address;
        }

        if (isNewAddress)
        {
            _iphone.Connected = true;
            Log($"iPhone conectado desde {address}.");
        }

        _iphone.LastUpdate = DateTime.UtcNow;
    }

    private void OnRecenterRequested()
    {
        Recenter("iPhone");
    }

    private void WriteLoop(CancellationToken cancellationToken)
    {
        // Windows usa por defecto una resolución de temporizador de ~15,6 ms,
        // insuficiente para 250 Hz. Con timeBeginPeriod(1) Thread.Sleep(4)
        // duerme ~4 ms en lugar de ~15,6 ms.
        timeBeginPeriod(1);

        try
        {
            const int periodMs = 4;

            while (!cancellationToken.IsCancellationRequested)
            {
                bool fresh = _fusion.HasFreshPose;
                _iphone.Connected = fresh;
                _iphone.SampleRateHz = fresh ? 60 : 0;

                PoseSample pose = _fusion.CurrentPose;
                _writer.Write(pose);

                Interlocked.Increment(ref _writeCounter);
                Interlocked.Increment(ref _rateWindowCount);

                DateTime now = DateTime.UtcNow;

                if ((now - _rateWindowStart).TotalSeconds >= 1.0)
                {
                    double seconds = (now - _rateWindowStart).TotalSeconds;
                    long count = Interlocked.Exchange(ref _rateWindowCount, 0);

                    lock (_gate)
                    {
                        _writeRateHz = count / seconds;
                    }

                    _rateWindowStart = now;
                }

                if ((now - _lastTelemetry).TotalMilliseconds >= 200)
                {
                    _lastTelemetry = now;
                    SendTelemetry(pose);
                }

                Thread.Sleep(periodMs);
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR en el bucle de salida: {ex.Message}");
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    private void SendTelemetry(PoseSample pose)
    {
        IPAddress? address;

        lock (_gate)
        {
            address = _lastAddress;
        }

        if (address is null)
            return;

        try
        {
            _telemetry.Send(address, _headphones, _iphone, pose, _writer.IsOpen);
        }
        catch (Exception ex)
        {
            Log($"No se pudo enviar telemetría: {ex.Message}");
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    private static void timeBeginPeriod(uint milliseconds) => TimeBeginPeriod(milliseconds);

    private static void timeEndPeriod(uint milliseconds) => TimeEndPeriod(milliseconds);

    /// <summary>
    /// Ajustes de ejes/suavizado en simtrack.settings.json (junto al .exe).
    /// Si el archivo no existe se genera uno con los valores por defecto.
    /// </summary>
    private void LoadSettings()
    {
        string path = Path.Combine(AppContext.BaseDirectory, SettingsFileName);

        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(_fusion.Settings, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            PoseSettings? loaded = JsonSerializer.Deserialize<PoseSettings>(File.ReadAllText(path));

            if (loaded is null)
                return;

            PoseSettings target = _fusion.Settings;
            target.InvertX = loaded.InvertX;
            target.InvertY = loaded.InvertY;
            target.InvertZ = loaded.InvertZ;
            target.InvertYaw = loaded.InvertYaw;
            target.InvertPitch = loaded.InvertPitch;
            target.InvertRoll = loaded.InvertRoll;
            target.RotationSmoothing = loaded.RotationSmoothing;
            target.PositionSmoothing = loaded.PositionSmoothing;
        }
        catch (Exception ex)
        {
            Log($"No se pudo leer {SettingsFileName}: {ex.Message}");
        }
    }

    private void Log(string message)
    {
        LogReceived?.Invoke($"{DateTime.Now:HH:mm:ss}  {message}");
    }

    public void Dispose()
    {
        Stop();
        _telemetry.Dispose();
        _receiver.Dispose();
        _writer.Dispose();
    }
}
