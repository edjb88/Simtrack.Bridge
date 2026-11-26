using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Simtrack.Core.Models;
using Simtrack.Core.Tracking;

namespace Simtrack.Communication;

/// <summary>
/// Envía la telemetría del PC al iPhone por el puerto 4244.
/// La app iOS espera JSON con estas claves exactas:
///   type = "headphones.telemetry", connected, deviceName, imuAvailable,
///   sampleRateHz, yaw, pitch, roll, gyroX/Y/Z, accelerometerX/Y/Z.
/// Las claves extra se ignoran en iOS, así que se añaden datos de estado.
/// </summary>
public sealed class TelemetrySender : IDisposable
{
    public const int TelemetryPort = 4244;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly UdpClient _client = new(AddressFamily.InterNetwork);

    public void Send(
        IPAddress iphoneAddress,
        SensorStatus headphones,
        SensorStatus iphone,
        PoseSample pose,
        bool freeTrackActive)
    {
        var message = new
        {
            protocol = "simtrack.bridge.v1",
            type = "headphones.telemetry",

            connected = headphones.Connected,
            deviceName = headphones.DeviceName,
            imuAvailable = headphones.ImuAvailable,
            sampleRateHz = headphones.SampleRateHz,

            yaw = pose.Yaw,
            pitch = pose.Pitch,
            roll = pose.Roll,

            gyroX = 0.0,
            gyroY = 0.0,
            gyroZ = 0.0,

            accelerometerX = 0.0,
            accelerometerY = 0.0,
            accelerometerZ = 0.0,

            iphoneConnected = iphone.Connected,
            iphoneSampleRateHz = iphone.SampleRateHz,
            freeTrackActive
        };

        string json = JsonSerializer.Serialize(message, JsonOptions);
        byte[] data = Encoding.UTF8.GetBytes(json);

        _client.Send(data, data.Length, new IPEndPoint(iphoneAddress, TelemetryPort));
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
