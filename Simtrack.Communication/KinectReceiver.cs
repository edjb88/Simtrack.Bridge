using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Simtrack.Core.Models;

namespace Simtrack.Communication;

/// <summary>
/// Recibe la pose del puente Kinect (Simtrack.KinectHost.exe) por UDP 4246.
/// Mismo formato de 48 bytes que el iPhone: 6 double little-endian
/// (X, Y, Z en centímetros y Yaw, Pitch, Roll en grados).
/// </summary>
public sealed class KinectReceiver : IDisposable
{
    public const int PosePort = 4246;
    public const int PacketSize = 48;

    private readonly object _gate = new();

    private UdpClient? _client;
    private CancellationTokenSource? _cancellation;
    private Task? _task;

    private long _packets;
    private long _rejected;
    private DateTime _lastPacketAt = DateTime.MinValue;

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

    public void Start()
    {
        lock (_gate)
        {
            if (_cancellation is not null)
                return;

            var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.ReceiveBufferSize = 64 * 1024;
            client.Client.Bind(new IPEndPoint(IPAddress.Loopback, PosePort));

            _client = client;
            _cancellation = new CancellationTokenSource();
            _task = ReceiveAsync(client, _cancellation.Token);

            LogReceived?.Invoke($"Escuchando pose del Kinect en UDP {PosePort}.");
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
            LogReceived?.Invoke($"Error en la pose del Kinect: {ex.Message}");
        }
    }

    private static HeadPose? TryParse(byte[] data)
    {
        if (data.Length != PacketSize)
            return null;

        var values = new double[6];

        for (var i = 0; i < 6; i++)
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
            ReceivedAt = DateTime.UtcNow,
            Source = "Kinect"
        };
    }

    public void Dispose()
    {
        Stop();
    }
}
