using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Simtrack.Core.Models;

namespace Simtrack.Communication;

/// <summary>
/// Recibe la pose de un tracker externo compatible con OpenTrack (UDP).
/// OpenTrack envia 48 bytes: 6 double little-endian (x, y, z, yaw, pitch, roll)
/// con la posicion en metros y los angulos en radianes o grados segun version.
/// Permite usar KinectCam (camara IR del Kinect) + OpenTrack como fuente.
/// </summary>
public sealed class ExternalPoseReceiver : IDisposable
{
    public const int DefaultPort = 4247;

    private readonly object _gate = new();
    private readonly int _port;

    private UdpClient? _client;
    private CancellationTokenSource? _cancellation;
    private Task? _task;

    private long _packets;
    private long _rejected;
    private DateTime _lastPacketAt = DateTime.MinValue;
    private IPAddress? _remoteAddress;

    public ExternalPoseReceiver(int port = DefaultPort)
    {
        _port = port;
    }

    public event Action<HeadPose>? PoseReceived;
    public event Action<string>? LogReceived;

    public bool IsRunning
    {
        get { lock (_gate) { return _cancellation is not null; } }
    }

    public long PacketsReceived => Interlocked.Read(ref _packets);
    public long PacketsRejected => Interlocked.Read(ref _rejected);

    public DateTime LastPacketAt
    {
        get { lock (_gate) { return _lastPacketAt; } }
    }

    public IPAddress? RemoteAddress
    {
        get { lock (_gate) { return _remoteAddress; } }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_cancellation is not null)
                return;

            var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.ReceiveBufferSize = 64 * 1024;
            client.Client.Bind(new IPEndPoint(IPAddress.Any, _port));

            _client = client;
            _cancellation = new CancellationTokenSource();
            _task = ReceiveAsync(client, _cancellation.Token);

            LogReceived?.Invoke($"Escuchando tracker externo (OpenTrack) en UDP {_port}.");
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        UdpClient? client;
        Task? task;

        lock (_gate)
        {
            cancellation = _cancellation;
            client = _client;
            task = _task;

            _cancellation = null;
            _client = null;
            _task = null;
        }

        if (cancellation is null)
            return;

        cancellation.Cancel();
        client?.Dispose();

        try
        {
            task?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        cancellation.Dispose();
    }

    private async Task ReceiveAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                HeadPose? pose = TryParse(result.Buffer);

                if (pose is null)
                {
                    Interlocked.Increment(ref _rejected);
                    continue;
                }

                Interlocked.Increment(ref _packets);

                lock (_gate)
                {
                    _lastPacketAt = DateTime.UtcNow;
                    _remoteAddress = result.RemoteEndPoint.Address;
                }

                PoseReceived?.Invoke(pose);
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
            LogReceived?.Invoke($"Error en tracker externo: {ex.Message}");
        }
    }

    private static HeadPose? TryParse(byte[] data)
    {
        if (data.Length < 48)
            return null;

        var values = new double[6];

        for (var i = 0; i < 6; i++)
        {
            long bits = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(i * 8, 8));
            values[i] = BitConverter.Int64BitsToDouble(bits);

            if (!double.IsFinite(values[i]))
                return null;
        }

        // OpenTrack usa metros; si los valores son pequeños se asume metro y
        // se pasa a centímetros. Los ángulos pueden venir en radianes o grados.
        double x = values[0];
        double y = values[1];
        double z = values[2];

        if (Math.Abs(x) < 10 && Math.Abs(y) < 10 && Math.Abs(z) < 10)
        {
            x *= 100;
            y *= 100;
            z *= 100;
        }

        double yaw = values[3];
        double pitch = values[4];
        double roll = values[5];

        const double degToRad = Math.PI / 180.0;

        if (Math.Abs(yaw) <= 4 && Math.Abs(pitch) <= 4 && Math.Abs(roll) <= 4)
        {
            yaw /= degToRad;
            pitch /= degToRad;
            roll /= degToRad;
        }

        return new HeadPose
        {
            X = x,
            Y = y,
            Z = z,
            YawDeg = yaw,
            PitchDeg = pitch,
            RollDeg = roll,
            ReceivedAt = DateTime.UtcNow,
            Source = "OpenTrack"
        };
    }

    public void Dispose()
    {
        Stop();
    }
}
