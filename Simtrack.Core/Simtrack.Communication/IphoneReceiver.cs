
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Simtrack.Core.Models;

namespace Simtrack.Communication;

public sealed class IphoneReceiver : IDisposable
{
    public const int TrackingPort = 4242;
    public const int ControlPort = 4243;

    private UdpClient? _trackingClient;
    private UdpClient? _controlClient;
    private CancellationTokenSource? _cancellation;
    private Task? _trackingTask;
    private Task? _controlTask;

    public event Action<HeadPose, IPAddress>? PoseReceived;
    public event Action<string>? ControlReceived;
    public event Action<string>? LogReceived;

    public bool IsRunning => _cancellation is not null;

    public void Start()
    {
        if (IsRunning)
            return;

        _cancellation = new CancellationTokenSource();

        _trackingClient = new UdpClient(TrackingPort);
        _controlClient = new UdpClient(ControlPort);

        _trackingTask = ReceiveTrackingAsync(_cancellation.Token);
        _controlTask = ReceiveControlAsync(_cancellation.Token);

        LogReceived?.Invoke(
            $"Receptor UDP iniciado. Tracking: {TrackingPort}, Control: {ControlPort}");
    }

    private async Task ReceiveTrackingAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result =
                    await _trackingClient!.ReceiveAsync(cancellationToken);

                byte[] data = result.Buffer;

                if (data.Length != 48)
                {
                    LogReceived?.Invoke(
                        $"Paquete rechazado: {data.Length} bytes. Se esperaban 48.");
                    continue;
                }

                double[] values = new double[6];

                for (int i = 0; i < 6; i++)
                {
                    long bits = BinaryPrimitives.ReadInt64LittleEndian(
                        data.AsSpan(i * 8, 8));

                    values[i] = BitConverter.Int64BitsToDouble(bits);
                }

                var pose = new HeadPose
                {
                    X = values[0],
                    Y = values[1],
                    Z = values[2],
                    Yaw = values[3],
                    Pitch = values[4],
                    Roll = values[5],
                    ReceivedAt = DateTime.Now
                };

                PoseReceived?.Invoke(pose, result.RemoteEndPoint.Address);
            }
        }
        catch (OperationCanceledException)
        {
            // Cierre normal.
        }
        catch (ObjectDisposedException)
        {
            // Cierre normal del socket.
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"Error en tracking UDP: {ex.Message}");
        }
    }

    private async Task ReceiveControlAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result =
                    await _controlClient!.ReceiveAsync(cancellationToken);

                string json = System.Text.Encoding.UTF8.GetString(result.Buffer);

                try
                {
                    using JsonDocument document = JsonDocument.Parse(json);

                    JsonElement root = document.RootElement;

                    string? protocol = root.TryGetProperty("protocol", out var p)
                        ? p.GetString()
                        : null;

                    string? type = root.TryGetProperty("type", out var t)
                        ? t.GetString()
                        : null;

                    if (protocol != "simtrack.bridge.v1")
                    {
                        LogReceived?.Invoke("Control recibido con protocolo desconocido.");
                        continue;
                    }

                    if (type == "tracking.recenter")
                    {
                        ControlReceived?.Invoke(
                            $"RE-CENTRADO solicitado desde {result.RemoteEndPoint.Address}");

                        LogReceived?.Invoke(
                            $"Orden de recentrado recibida: {json}");
                    }
                    else
                    {
                        LogReceived?.Invoke(
                            $"Mensaje de control no reconocido: {json}");
                    }
                }
                catch (JsonException)
                {
                    LogReceived?.Invoke("Se recibió un mensaje de control JSON inválido.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cierre normal.
        }
        catch (ObjectDisposedException)
        {
            // Cierre normal.
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"Error en control UDP: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_cancellation is null)
            return;

        _cancellation.Cancel();

        _trackingClient?.Dispose();
        _controlClient?.Dispose();

        _trackingClient = null;
        _controlClient = null;

        _cancellation.Dispose();
        _cancellation = null;

        LogReceived?.Invoke("Receptor UDP detenido.");
    }

    public void Dispose()
    {
        Stop();
    }
}
