
namespace Simtrack.Core.Models;

public sealed class ImuData
{
    public double Yaw { get; init; }
    public double Pitch { get; init; }
    public double Roll { get; init; }

    public double GyroX { get; init; }
    public double GyroY { get; init; }
    public double GyroZ { get; init; }

    public double AccelerometerX { get; init; }
    public double AccelerometerY { get; init; }
    public double AccelerometerZ { get; init; }

    public double SampleRateHz { get; init; }
    public DateTime ReceivedAt { get; init; }
}
