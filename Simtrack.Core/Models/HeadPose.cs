namespace Simtrack.Core.Models;

/// <summary>
/// Pose tal y como la envía la app del iPhone.
/// Unidades del paquete UDP de 48 bytes:
///   X, Y, Z  → centímetros
///   Yaw, Pitch, Roll → GRADOS (no radianes)
/// La app iOS ya las envía relativas a su referencia de recentrado.
/// </summary>
public sealed class HeadPose
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }

    public double YawDeg { get; init; }
    public double PitchDeg { get; init; }
    public double RollDeg { get; init; }

    public DateTime ReceivedAt { get; init; }

    public string Source { get; init; } = "iPhone";

    public override string ToString()
    {
        return $"X: {X:F2} cm | Y: {Y:F2} cm | Z: {Z:F2} cm | " +
               $"Yaw: {YawDeg:F1}° | Pitch: {PitchDeg:F1}° | Roll: {RollDeg:F1}°";
    }
}
