namespace Simtrack.Core.Tracking;

/// <summary>
/// Pose normalizada lista para volcar al bucle de salida.
/// Unidades: rotaciones en RADIANES y traslaciones en CENTÍMETROS,
/// que es exactamente lo que espera FreeTrack 2.0 (FT_SharedMem).
/// </summary>
public readonly record struct PoseSample(
    double Yaw,
    double Pitch,
    double Roll,
    double X,
    double Y,
    double Z)
{
    public static PoseSample Zero => new(0, 0, 0, 0, 0, 0);

    public string ToDisplayString()
    {
        return $"X {X,6:F1} | Y {Y,6:F1} | Z {Z,6:F1} cm   " +
               $"Yaw {Yaw * 180 / Math.PI,6:F1}° | " +
               $"Pitch {Pitch * 180 / Math.PI,6:F1}° | " +
               $"Roll {Roll * 180 / Math.PI,6:F1}°";
    }
}
