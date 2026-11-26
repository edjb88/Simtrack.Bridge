namespace Simtrack.Core.Models;

/// <summary>
/// Muestra de la IMU de los auriculares (Sony ULT WEAR u otros).
/// Todavía no hay integración con el Sony Head Tracker; este modelo
/// deja el contrato preparado para la fase de fusión.
/// </summary>
public sealed class ImuData
{
    public double YawDeg { get; init; }
    public double PitchDeg { get; init; }
    public double RollDeg { get; init; }

    /// <summary>Velocidad angular en grados por segundo.</summary>
    public double GyroXDegPerSecond { get; init; }
    public double GyroYDegPerSecond { get; init; }
    public double GyroZDegPerSecond { get; init; }

    /// <summary>Aceleración en g.</summary>
    public double AccelX { get; init; }
    public double AccelY { get; init; }
    public double AccelZ { get; init; }

    public double SampleRateHz { get; init; }
    public DateTime ReceivedAt { get; init; }
}
