namespace Simtrack.Core.Models;

/// <summary>Estado de un sensor de tracking (iPhone, auriculares, etc.).</summary>
public sealed class SensorStatus
{
    public bool Connected { get; set; }
    public string DeviceName { get; set; } = "No conectado";
    public bool ImuAvailable { get; set; }
    public double SampleRateHz { get; set; }
    public DateTime? LastUpdate { get; set; }
}
