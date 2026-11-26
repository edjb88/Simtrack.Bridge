
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Simtrack.Core.Models;

namespace Simtrack.Communication;

public sealed class TelemetryServer : IDisposable
{
    public const int TelemetryPort = 4244;

    private readonly UdpClient _client = new();

    public async Task SendAsync(IPAddress iphoneAddress, SensorStatus status)
    {
        var message = new
        {
            protocol = "simtrack.bridge.v1",
            type = "headphones.telemetry",

            connected = status.Connected,
            deviceName = status.DeviceName,
            imuAvailable = status.ImuAvailable,
            sampleRateHz = status.SampleRateHz,

            yaw = 0.0,
            pitch = 0.0,
            roll = 0.0,

            gyroX = 0.0,
            gyroY = 0.0,
            gyroZ = 0.0,

            accelerometerX = 0.0,
            accelerometerY = 0.0,
            accelerometerZ = 0.0
        };

        string json = JsonSerializer.Serialize(message);
        byte[] data = Encoding.UTF8.GetBytes(json);

        await _client.SendAsync(
            data,
            new IPEndPoint(iphoneAddress, TelemetryPort));
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
