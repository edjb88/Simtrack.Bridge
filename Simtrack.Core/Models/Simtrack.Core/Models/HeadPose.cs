
namespace Simtrack.Core.Models;

public sealed class HeadPose
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }

    public double Yaw { get; init; }
    public double Pitch { get; init; }
    public double Roll { get; init; }

    public DateTime ReceivedAt { get; init; }

    public string Source { get; init; } = "iPhone";

    public override string ToString()
    {
        return $"X: {X:F2} | Y: {Y:F2} | Z: {Z:F2} | " +
               $"Yaw: {Yaw:F2} | Pitch: {Pitch:F2} | Roll: {Roll:F2}";
    }
}
