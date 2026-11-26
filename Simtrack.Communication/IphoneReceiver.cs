using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Simtrack.Core.Models;

namespace Simtrack.Communication;

/// <summary>
/// Receptor UDP del iPhone.
///
///   Puerto 4242 → pose 6DOF: 6 double little-endian (48 bytes)
///                 X, Y, Z en centímetros y Yaw, Pitch, Roll en GRADOS.
///   Puerto 4243 → control JSON: { "protocol": "simtrack.bridge.v1",
///                 "type": "tracking.recenter", ... }
/// </summary>
public sealed class IphoneReceiver : IDisposable
{
    public const int TrackingPort = 4242;
    public const int ControlPort = 4243;
    public const int PosePacketSize = 48;

    private readonly object _gate = new();

    private UdpClient? _trackingClient;
    private UdpClient? _controlClient;
    private CancellationTokenSource? _cancellation;
    private Task? _trackingTask;
    private Task? _controlTask;

    private long _packetsReceived;
    private long _packetsRejected;
    private IPAddress? _lastAddress;

    public event Action<HeadPose, IPAddress>? PoseReceived;
    public event Action? RecenterRequested;
    public event Action<string>? LogReceived;

    public bool IsRunning
    {
        get { lock (_gate) { return _cancellation is not null; } }
    }

    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);
    public long PacketsRejected => Interlocked.Read(ref _packetsRejected);

    public IPAddress? LastAddress
    {
        get { lock (_gate) { return _lastAddress; } }
    }

    /// <summary>
    /// Abre los dos puertos UDP. Si algo falla (por ejemplo, OpenTrack ya
    /// está usando el 4242) lanza una excepción con un mensaje entendible.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_cancellation is not null)
                return;

            try
            {
                _trackingClient = CreateClient(TrackingPort);
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException(
                    $"No se pudo abrir el puerto UDP {TrackingPort}. " +
                    $"¿Está OpenTrack u otra app usándolo? Detalle: {ex.Message}", ex);
            }

            try
            {
                _controlClient = CreateClient(ControlPort);
            }
            catch (SocketException ex)
            {
                _trackingClient.Dispose();
                _trackingClient = null;

                throw new InvalidOperationException(
                    $"No se pudo abrir el puerto UDP {ControlPort}. Detalle: {ex.Message}", ex);
            }

            _cancellation = new CancellationTokenSource();

            _trackingTask = ReceiveTrackingAsync(_trackingClient, _cancellation.Token);
            _controlTask = ReceiveControlAsync(_controlClient, _cancellation.Token);

            LogReceived?.Invoke(
                $"Escuchando UDP: tracking {TrackingPort}, control {ControlPort}.");
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        UdpClient? tracking;
        UdpClient? control;
        Task? trackingTask;
        Task? controlTask;

        lock (_gate)
        {
            cancellation = _cancellation;
            tracking = _trackingClient;
            control = _controlClient;
            trackingTask = _trackingTask;
            controlTask = _controlTask;

            _cancellation = null;
            _trackingClient = null;
            _controlClient = null;
            _trackingTask = null;
            _controlTask = null;
        }

        if (cancellation is null)
            return;

        cancellation.Cancel();
        tracking?.Dispose();
        control?.Dispose();

        try
        {
            Task.WaitAll(
                new[] { trackingTask, controlTask }.OfType<Task>().ToArray(),
                TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        cancellation.Dispose();

        LogReceived?.Invoke("Receptor UDP detenido.");
    }

    private static UdpClient CreateClient(int port)
    {
        var client = new UdpClient(AddressFamily.InterNetwork);

        // Buffer generoso: el iPhone envía a ~60 Hz y no queremos perder
        // ráfagas si un momento el hilo va cargado.
        client.Client.ReceiveBufferSize = 64 * 1024;
        client.Client.Bind(new IPEndPoint(IPAddress.Any, port));

        return client;
    }

    private async Task ReceiveTrackingAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                HeadPose? pose = TryParsePose(result.Buffer);

                if (pose is null)
                {
                    Interlocked.Increment(ref _packetsRejected);
                    LogReceived?.Invoke(
                        $"Paquete descartado: {result.Buffer.Length} bytes " +
                        $"(se esperaban {PosePacketSize} con 6 double válidos).");
                    continue;
                }

                Interlocked.Increment(ref _packetsReceived);

                lock (_gate)
                {
                    _lastAddress = result.RemoteEndPoint.Address;
                }

                PoseReceived?.Invoke(pose, result.RemoteEndPoint.Address);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException ex)
        {
            LogReceived?.Invoke($"Socket de tracking cerrado: {ex.Message}");
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"Error en tracking UDP: {ex.Message}");
        }
    }

    private static HeadPose? TryParsePose(byte[] data)
    {
        if (data.Length != PosePacketSize)
            return null;

        Span<double> values = stackalloc double[6];

        for (var i = 0; i < values.Length; i++)
        {
            long bits = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(i * 8, 8));
            values[i] = BitConverter.Int64BitsToDouble(bits);

            if (!double.IsFinite(values[i]))
                return null;
        }

        return new HeadPose
        {
            X = values[0],
            Y = values[1],
            Z = values[2],
            YawDeg = values[3],
            PitchDeg = values[4],
            RollDeg = values[5],
            ReceivedAt = DateTime.UtcNow
        };
    }

    private async Task ReceiveControlAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                string json = Encoding.UTF8.GetString(result.Buffer);

                try
                {
                    using JsonDocument document = JsonDocument.Parse(json);
                    JsonElement root = document.RootElement;

                    if (!root.TryGetProperty("protocol", out JsonElement protocolElement) ||
                        protocolElement.GetString() != "simtrack.bridge.v1")
                    {
                        LogReceived?.Invoke("Control descartado: protocolo desconocido.");
                        continue;
                    }

                    string? type = root.TryGetProperty("type", out JsonElement typeElement)
                        ? typeElement.GetString()
                        : null;

                    switch (type)
                    {
                        case "tracking.recenter":
                            LogReceived?.Invoke(
                                $"Recentrado solicitado desde {result.RemoteEndPoint.Address}.");
                            RecenterRequested?.Invoke();
                            break;

                        default:
                            LogReceived?.Invoke($"Control no reconocido: {type ?? "(sin type)"}.");
                            break;
                    }
                }
                catch (JsonException)
                {
                    LogReceived?.Invoke("Control descartado: JSON inválido.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"Error en control UDP: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
