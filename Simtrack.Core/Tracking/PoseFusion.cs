using Simtrack.Core.Models;

namespace Simtrack.Core.Tracking;

/// <summary>Ajustes de mapeo entre ARKit (iPhone) y la convención de TrackIR.</summary>
public sealed class PoseSettings
{
    /// <summary>
    /// ARKit usa "X a la derecha de la cámara": al girar la cabeza hacia tu
    /// derecha la X disminuye. TrackIR espera lo contrario, así que por
    /// defecto se invierte.
    /// </summary>
    public bool InvertX { get; set; } = true;

    public bool InvertY { get; set; }
    public bool InvertZ { get; set; }
    public bool InvertYaw { get; set; }
    public bool InvertPitch { get; set; }
    public bool InvertRoll { get; set; }

    /// <summary>Suavizado exponencial aplicado a la rotación (0 = sin suavizado, 1 = congelado).</summary>
    public double RotationSmoothing { get; set; } = 0.15;

    /// <summary>Suavizado exponencial aplicado a la posición.</summary>
    public double PositionSmoothing { get; set; } = 0.25;

    /// <summary>
    /// Modo de fusión: "auto" (posición del Kinect + rotación del iPhone si
    /// están los dos), "kinect" (todo del Kinect) o "iphone" (todo del iPhone).
    /// </summary>
    public string FusionMode { get; set; } = "auto";
}

/// <summary>
/// Convierte y normaliza la pose del iPhone.
///
/// El paquete del iPhone trae grados + centímetros ya relativos a su propia
/// referencia de recentrado, así que aquí solo hay que:
///   1. pasar grados a radianes,
///   2. aplicar inversiones de eje,
///   3. restar la referencia de recentrado del lado Windows,
///   4. suavizar para quitar el temblor del tracking facial.
///
/// La fusión real con la IMU de los auriculares (filtro complementario) se
/// añadirá en <see cref="ApplyImu"/> cuando exista la fuente Sony.
/// </summary>
public sealed class PoseFusion
{
    private readonly object _gate = new();

    private PoseSample _raw = PoseSample.Zero;
    private PoseSample _smoothed = PoseSample.Zero;
    private PoseSample _recenterOffset = PoseSample.Zero;
    private bool _baselineCaptured;

    private long _sampleCount;
    private DateTime _lastSampleAt = DateTime.MinValue;

    public PoseSettings Settings { get; } = new();

    /// <summary>Se considera que el iPhone está conectado si llegó algo en este margen.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMilliseconds(500);

    public long SampleCount
    {
        get { lock (_gate) { return _sampleCount; } }
    }

    public DateTime LastSampleAt
    {
        get { lock (_gate) { return _lastSampleAt; } }
    }

    public bool HasFreshPose
    {
        get
        {
            lock (_gate)
            {
                return _lastSampleAt != DateTime.MinValue &&
                       DateTime.UtcNow - _lastSampleAt <= StaleAfter;
            }
        }
    }

    /// <summary>Pose final que se escribe en FreeTrack (radianes + centímetros).</summary>
    public PoseSample CurrentPose
    {
        get
        {
            lock (_gate)
            {
                return new PoseSample(
                    _smoothed.Yaw - _recenterOffset.Yaw,
                    _smoothed.Pitch - _recenterOffset.Pitch,
                    _smoothed.Roll - _recenterOffset.Roll,
                    _smoothed.X - _recenterOffset.X,
                    _smoothed.Y - _recenterOffset.Y,
                    _smoothed.Z - _recenterOffset.Z);
            }
        }
    }

    /// <summary>Pose del iPhone tal cual (radianes + cm), sin recentrado ni suavizado.</summary>
    public PoseSample RawPose
    {
        get { lock (_gate) { return _raw; } }
    }

    public void ApplyIphonePose(HeadPose pose)
    {
        if (pose is null)
            return;

        const double degToRad = Math.PI / 180.0;

        double yaw = pose.YawDeg * degToRad;
        double pitch = pose.PitchDeg * degToRad;
        double roll = pose.RollDeg * degToRad;
        double x = pose.X;
        double y = pose.Y;
        double z = pose.Z;

        PoseSettings settings = Settings;

        if (settings.InvertYaw) yaw = -yaw;
        if (settings.InvertPitch) pitch = -pitch;
        if (settings.InvertRoll) roll = -roll;
        if (settings.InvertX) x = -x;
        if (settings.InvertY) y = -y;
        if (settings.InvertZ) z = -z;

        var sample = new PoseSample(yaw, pitch, roll, x, y, z);

        lock (_gate)
        {
            _raw = sample;

            double rotationAlpha = Math.Clamp(settings.RotationSmoothing, 0.0, 0.95);
            double positionAlpha = Math.Clamp(settings.PositionSmoothing, 0.0, 0.95);

            _smoothed = new PoseSample(
                LowPass(_smoothed.Yaw, sample.Yaw, rotationAlpha),
                LowPass(_smoothed.Pitch, sample.Pitch, rotationAlpha),
                LowPass(_smoothed.Roll, sample.Roll, rotationAlpha),
                LowPass(_smoothed.X, sample.X, positionAlpha),
                LowPass(_smoothed.Y, sample.Y, positionAlpha),
                LowPass(_smoothed.Z, sample.Z, positionAlpha));

            if (!_baselineCaptured)
            {
                // Primera muestra: se toma como cero para que la pose empiece
                // centrada (el Kinect manda coordenadas absolutas del sensor).
                _recenterOffset = _smoothed;
                _baselineCaptured = true;
            }

            _sampleCount++;
            _lastSampleAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Marca la orientación actual como centro. La app iOS ya recentra por su
    /// cuenta; esto vuelve a centrar en el lado Windows (útil si se ha movido
    /// el móvil o si la pose llega desplazada).
    /// </summary>
    public void Recenter()
    {
        lock (_gate)
        {
            _recenterOffset = _smoothed;
            _baselineCaptured = true;
        }
    }

    /// <summary>
    /// Reservado para la fase Sony: rotación de la IMU de baja latencia.
    /// Sin fuente real todavía, no altera la salida.
    /// </summary>
    public void ApplyImu(ImuData imu)
    {
        _ = imu;
    }

    /// <summary>Filtro exponencial: alpha alto = más suave.</summary>
    private static double LowPass(double previous, double next, double alpha)
    {
        return previous + (next - previous) * (1.0 - alpha);
    }
}
