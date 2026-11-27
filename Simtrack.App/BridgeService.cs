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
    private readonly KinectReceiver _kinect = new();
    private readonly ExternalPoseReceiver _external = new();
    private readonly PoseFusion _fusionIphone = new();
    private readonly PoseFusion _fusionKinect = new();
    private readonly PoseFusion _fusionExternal = new();
    private readonly FreeTrackWriter _writer = new();
    private readonly TelemetrySender _telemetry = new();
    private readonly DiscoveryResponder _discovery = new();

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

    private string _activeSource = "ninguna";
    private HeadPose? _externalPose;
    private DateTime _externalAt = DateTime.MinValue;
    private bool _externalLogged;
    private string _fusionMode = "auto";
    private string _kinectMode = "sdk";
    private string _loggedSource = "ninguna";
    private bool _kinectConnectedLogged;
    private System.Diagnostics.Process? _kinectProcess;
    private DateTime _lastKinectWatchdog = DateTime.MinValue;

    private const string SettingsFileName = "simtrack.settings.json";

    public BridgeService()
    {
        LoadSettings();
    }

    public event Action<string>? LogReceived;

    /// <summary>Avisa de una orden ejecutada ("Windows" o "iPhone") para la interfaz.</summary>
    public event Action<string>? CommandReceived;

    public DateTime StartedAt { get; private set; }

    public PoseFusion Fusion => ActiveFusion;
    public bool IsRunning => _loopTask is { IsCompleted: false };
    public bool FreeTrackOpen => _writer.IsOpen;

    public long PacketsReceived => _receiver.PacketsReceived;
    public long PacketsRejected => _receiver.PacketsRejected;
    public long KinectPackets => _kinect.PacketsReceived;
    public double WriteRateHz { get { lock (_gate) { return _writeRateHz; } } }
    public IPAddress? RemoteAddress => _lastAddress;
    public PoseSample CurrentPose => ActiveFusion.CurrentPose;
    public bool IphoneFresh => _fusionIphone.HasFreshPose;
    public bool KinectFresh => _fusionKinect.HasFreshPose;
    public bool ExternalFresh => _fusionExternal.HasFreshPose;

    public bool HasKinectBridge => FindKinectHostExe() != null;

    public bool KinectPacketsFresh => _kinect.PacketsReceived > 0 && (DateTime.UtcNow - _kinect.LastPacketAt).TotalMilliseconds < 1500;

    public string ActiveSource
    {
        get { lock (_gate) { return _activeSource; } }
    }

    public DateTime LastWriteAt => _writer.LastWriteAt;

    /// <summary>Ajustes en uso (los mismos para Kinect e iPhone).</summary>
    public PoseSettings Settings => _fusionIphone.Settings;

    /// <summary>Copia los ajustes a todas las fuentes y los guarda en disco.</summary>
    public void ApplyCalibration()
    {
        CopySettings(_fusionIphone.Settings, _fusionKinect.Settings);
        SaveSettingsToDisk();
        Log("Calibracion aplicada: " + JsonSerializer.Serialize(_fusionIphone.Settings));
    }

    private void SaveSettingsToDisk()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(_fusionIphone.Settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log($"No se pudieron guardar los ajustes: {ex.Message}");
        }
    }

    /// <summary>Kinect manda si tiene datos frescos; si no, el iPhone.</summary>
    private PoseFusion ActiveFusion =>
        _fusionExternal.HasFreshPose ? _fusionExternal :
        (_fusionKinect.HasFreshPose ? _fusionKinect : _fusionIphone);

    public void Start()
    {
        if (IsRunning)
            return;

        _receiver.LogReceived += OnReceiverLog;
        _receiver.PoseReceived += OnPoseReceived;
        _receiver.RecenterRequested += OnRecenterRequested;

        _kinect.LogReceived += OnReceiverLog;
        _kinect.PoseReceived += OnKinectPoseReceived;

        _external.LogReceived += OnReceiverLog;
        _external.PoseReceived += OnExternalPoseReceived;

        _discovery.LogReceived += OnReceiverLog;
        _discovery.SimHubPort = DetectSimHubPort();

        try
        {
            _writer.Open();
            Log($"Memoria compartida {FreeTrackLayout.SharedMemoryName} abierta " +
                $"({FreeTrackLayout.HeapSize} bytes, FTData {FreeTrackLayout.DataSize} bytes).");

            _receiver.Start();
            _kinect.Start();
            _external.Start();
            _discovery.Start();

            TryStartKinectHost();
        }
        catch (Exception ex)
        {
            Log($"ERROR al arrancar: {ex.Message}");

            _discovery.Stop();
            _kinect.Stop();
            _writer.Close();
            DetachHandlers();

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

        cancellation?.Cancel();

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        cancellation?.Dispose();

        _discovery.Stop();
        DetachHandlers();
        _receiver.Stop();
        _kinect.Stop();
        _external.Stop();
        StopKinectHost();

        _writer.Close();

        Log("Motor de salida detenido.");
    }

    public void Recenter(string origin = "Windows")
    {
        _fusionKinect.Recenter();
        _fusionIphone.Recenter();
        _fusionExternal.Recenter();

        PoseSample pose = ActiveFusion.CurrentPose;
        CommandReceived?.Invoke($"Recentrado desde {origin} · {DateTime.Now:HH:mm:ss}");
        Log($"Recentrado ({origin}): " +
            $"X {pose.X:F1} cm, Y {pose.Y:F1} cm, Z {pose.Z:F1} cm, " +
            $"Yaw {pose.Yaw * 180 / Math.PI:F1}°, " +
            $"Pitch {pose.Pitch * 180 / Math.PI:F1}°, " +
            $"Roll {pose.Roll * 180 / Math.PI:F1}°.");
    }

    private void DetachHandlers()
    {
        _receiver.LogReceived -= OnReceiverLog;
        _receiver.PoseReceived -= OnPoseReceived;
        _receiver.RecenterRequested -= OnRecenterRequested;
        _kinect.LogReceived -= OnReceiverLog;
        _kinect.PoseReceived -= OnKinectPoseReceived;
        _external.LogReceived -= OnReceiverLog;
        _external.PoseReceived -= OnExternalPoseReceived;
        _discovery.LogReceived -= OnReceiverLog;
    }

    private static string? FindKinectHostExe()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "Simtrack.KinectHost.exe"),
            Path.Combine(AppContext.BaseDirectory, "Kinect", "Simtrack.KinectHost.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Barrido del motor de inclinación: mueve el sensor y se queda con el
    /// ángulo en el que detecta el esqueleto. Necesita el sensor para él solo,
    /// así que para el puente y lo vuelve a arrancar al terminar.
    /// </summary>
    public (bool ok, string detail) CalibrateKinectMotors()
    {
        string? exe = FindKinectHostExe();

        if (exe is null)
            return (true, "Sin puente de Kinect instalado: se omite la calibración del motor.");

        StopKinectHost();
        Thread.Sleep(600);

        string logPath = Path.Combine(AppContext.BaseDirectory, "kinect-motor.log");

        try
        {
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
        catch
        {
        }

        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(exe, "--motor-calibrate --log=\"" + logPath + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = System.Diagnostics.Process.Start(info);

            bool finished = process?.WaitForExit(60000) ?? false;

            if (!finished)
            {
                try { process?.Kill(); } catch { }
                return (false, "El motor del Kinect no respondió a tiempo.");
            }

            string text = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;

            Log("Kinect motor: " + text.Replace("\r", " ").Replace("\n", " | "));

            int marker = text.IndexOf("MOTOR_OK", StringComparison.OrdinalIgnoreCase);

            if (marker < 0)
                return (false, "No se pudo completar el barrido del motor.");

            string summary = text.Substring(marker).Split('\n')[0].Trim();

            return summary.Contains("esqueletos=0")
                ? (false, "El sensor barrió todas las inclinaciones y no te detectó. Colócate a 1,3-1,5 m y repite.")
                : (true, summary);
        }
        catch (Exception ex)
        {
            return (false, "Error calibrando el motor: " + ex.Message);
        }
        finally
        {
            TryStartKinectHost();
        }
    }

    /// <summary>Arranca el puente del Kinect si está junto al ejecutable.</summary>
    private void TryStartKinectHost()
    {
        try
        {
            string? exe = FindKinectHostExe();

            if (exe is null)
            {
                Log("Kinect: puente no instalado (opcional). Se usa el iPhone.");
                return;
            }

            if (System.Diagnostics.Process.GetProcessesByName("Simtrack.KinectHost").Length > 0)
            {
                Log("Kinect: el puente ya estaba en marcha.");
                return;
            }

            string logPath = Path.Combine(AppContext.BaseDirectory, "kinect.log");
            // Con Face Tracking + head pose RGB (Haar + LBF) el puente
            // necesita los flujos de color y profundidad.
            string arguments = "--quiet --log=" + (logPath.Contains(" ") ? "\"" + logPath + "\"" : logPath);

            if (_kinectMode == "ir")
                arguments += " --ir";

            _kinectProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Log("Kinect: puente iniciado.");
        }
        catch (Exception ex)
        {
            Log($"Kinect: no se pudo iniciar el puente ({ex.Message}).");
        }
    }

    private void EnsureKinectHostAlive()
    {
        try
        {
            if (_kinectProcess != null && _kinectProcess.HasExited)
            {
                Log("Kinect: el puente se ha cerrado; reiniciando.");
                _kinectProcess.Dispose();
                _kinectProcess = null;
                TryStartKinectHost();
            }
        }
        catch
        {
        }
    }

    private void StopKinectHost()
    {
        try
        {
            if (_kinectProcess is { HasExited: false })
                _kinectProcess.Kill();

            _kinectProcess?.Dispose();
            _kinectProcess = null;
        }
        catch
        {
            _kinectProcess = null;
        }
    }

    /// <summary>
    /// Puerto de SimHub para el dashboard del iPhone: 8888 si está escuchando,
    /// si no el primer puerto razonable de su rango.
    /// </summary>
    private static int DetectSimHubPort()
    {
        try
        {
            var listeners = System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpListeners();

            if (listeners.Any(l => l.Port == 8888))
                return 8888;

            int port = listeners
                .Where(l => l.Port is >= 8000 and <= 9000)
                .Select(l => l.Port)
                .Distinct()
                .OrderBy(p => p)
                .FirstOrDefault();

            return port == 0 ? 8888 : port;
        }
        catch
        {
            return 8888;
        }
    }

    private void OnReceiverLog(string message)
    {
        Log(message);
    }

    private void OnPoseReceived(HeadPose pose, IPAddress address)
    {
        _fusionIphone.ApplyIphonePose(pose);

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

    private void OnKinectPoseReceived(HeadPose pose)
    {
        _fusionKinect.ApplyIphonePose(pose);

        if (!_kinectConnectedLogged)
        {
            _kinectConnectedLogged = true;
            Log("Kinect conectado: enviando pose por UDP 4246.");
        }
    }

    private void OnExternalPoseReceived(HeadPose pose)
    {
        _fusionExternal.ApplyIphonePose(pose);

        lock (_gate)
        {
            _externalPose = pose;
            _externalAt = pose.ReceivedAt;
        }

        if (!_externalLogged)
        {
            _externalLogged = true;
            Log("Tracker externo conectado (OpenTrack/KinectCam).");
        }
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
                bool externalFresh = _fusionExternal.HasFreshPose;
                bool kinectFresh = _fusionKinect.HasFreshPose;
                bool iphoneFresh = _fusionIphone.HasFreshPose;

                string source;

                if (externalFresh)
                    source = "OpenTrack";
                else if (_fusionMode == "kinect")
                    source = kinectFresh ? "Kinect" : (iphoneFresh ? "iPhone" : "ninguna");
                else if (_fusionMode == "iphone")
                    source = iphoneFresh ? "iPhone" : (kinectFresh ? "Kinect" : "ninguna");
                else
                    source = kinectFresh && iphoneFresh ? "Kinect+iPhone" : (kinectFresh ? "Kinect" : (iphoneFresh ? "iPhone" : "ninguna"));

                lock (_gate)
                {
                    _activeSource = source;
                }

                if (source != _loggedSource && source != "ninguna")
                {
                    _loggedSource = source;
                    Log($"Fuente de tracking activa: {source}.");
                }
                else if (source == "ninguna")
                {
                    _loggedSource = source;
                }

                _iphone.Connected = iphoneFresh;
                _iphone.SampleRateHz = iphoneFresh ? 60 : 0;

                PoseSample kinectPose = _fusionKinect.CurrentPose;
                PoseSample iphonePose = _fusionIphone.CurrentPose;
                PoseSample externalPose = _fusionExternal.CurrentPose;

                PoseSample pose;

                if (source == "OpenTrack")
                {
                    pose = externalPose;
                }
                else if (source == "Kinect+iPhone")
                {
                    // Posición del Kinect (muy estable) + rotación del iPhone
                    // (TrueDepth sí da yaw/pitch/roll fiables).
                    pose = new PoseSample(
                        iphonePose.Yaw,
                        iphonePose.Pitch,
                        iphonePose.Roll,
                        kinectPose.X,
                        kinectPose.Y,
                        kinectPose.Z);
                }
                else if (source == "Kinect")
                {
                    pose = kinectPose;
                }
                else
                {
                    pose = iphonePose;
                }

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

                if ((now - _lastKinectWatchdog).TotalSeconds >= 5)
                {
                    _lastKinectWatchdog = now;
                    EnsureKinectHostAlive();
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
            PoseSettings template = _fusionIphone.Settings;

            if (!File.Exists(path))
            {
                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                PoseSettings? loaded = JsonSerializer.Deserialize<PoseSettings>(File.ReadAllText(path));

                if (loaded is not null)
                    CopySettings(loaded, template);
            }

            CopySettings(template, _fusionKinect.Settings);
            CopySettings(template, _fusionExternal.Settings);
            _fusionMode = template.FusionMode ?? "auto";
            _kinectMode = template.KinectMode ?? "sdk";
        }
        catch (Exception ex)
        {
            Log($"No se pudo leer {SettingsFileName}: {ex.Message}");
        }
    }

    private static void CopySettings(PoseSettings from, PoseSettings to)
    {
        to.InvertX = from.InvertX;
        to.InvertY = from.InvertY;
        to.InvertZ = from.InvertZ;
        to.InvertYaw = from.InvertYaw;
        to.InvertPitch = from.InvertPitch;
        to.InvertRoll = from.InvertRoll;
        to.RotationSmoothing = from.RotationSmoothing;
        to.PositionSmoothing = from.PositionSmoothing;
        to.FusionMode = from.FusionMode;
        to.KinectMode = from.KinectMode;
    }

    private void Log(string message)
    {
        LogReceived?.Invoke($"{DateTime.Now:HH:mm:ss}  {message}");
    }

    public void Dispose()
    {
        Stop();
        StopKinectHost();
        _telemetry.Dispose();
        _kinect.Dispose();
        _external.Dispose();
        _receiver.Dispose();
        _writer.Dispose();
    }
}
