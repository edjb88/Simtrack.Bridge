using System;
using OpenCvSharp;

namespace Simtrack.Kinect
{
    /// <summary>
    /// Head pose a partir de la imagen RGB del Kinect, sin esqueleto.
    /// Detector YuNet + solvePnP con 5 puntos (misma idea que KINECT2TIR).
    /// </summary>
    internal sealed class RgbHeadPose : IDisposable
    {
        private readonly FaceDetectorYN _detector;
        private readonly int _width;
        private readonly int _height;
        private readonly byte[] _bgr;
        private readonly double[,] _camera;
        private readonly double[] _distortion;

        private static readonly Point3f[] ModelPoints =
        {
            new Point3f(-225f, 170f, -135f),
            new Point3f(225f, 170f, -135f),
            new Point3f(0f, 0f, 0f),
            new Point3f(-150f, -150f, -125f),
            new Point3f(150f, -150f, -125f)
        };

        public RgbHeadPose(string modelPath, int width, int height)
        {
            _width = width;
            _height = height;
            _bgr = new byte[width * height * 3];

            _detector = FaceDetectorYN.Create(modelPath, string.Empty, new Size(width, height), 0.6f, 0.3f, 5000);

            const double focal = 533.0; // ~62 grados horizontales en 640 px

            _camera = new double[3, 3];
            _camera[0, 0] = focal;
            _camera[1, 1] = focal;
            _camera[0, 2] = width / 2.0;
            _camera[1, 2] = height / 2.0;
            _camera[2, 2] = 1.0;

            _distortion = new double[5];
        }

        public bool TryEstimate(byte[] rgbx, out double x, out double y, out double z, out double yaw, out double pitch, out double roll)
        {
            x = 0;
            y = 0;
            z = 0;
            yaw = 0;
            pitch = 0;
            roll = 0;

            if (rgbx == null || rgbx.Length < _width * _height * 4)
                return false;

            for (var i = 0; i < _width * _height; i++)
            {
                int src = i * 4;

                _bgr[i * 3] = rgbx[src + 2];
                _bgr[i * 3 + 1] = rgbx[src + 1];
                _bgr[i * 3 + 2] = rgbx[src];
            }

            return TryEstimateBgr(_bgr, out x, out y, out z, out yaw, out pitch, out roll);
        }

        /// <summary>Prueba con una imagen BGR ya preparada (util para depurar).</summary>
        public bool TryEstimateBgr(byte[] bgr, out double x, out double y, out double z, out double yaw, out double pitch, out double roll)
        {
            x = 0;
            y = 0;
            z = 0;
            yaw = 0;
            pitch = 0;
            roll = 0;

            // Se aclara la imagen: la habitacion suele estar oscura.
            var boosted = new byte[bgr.Length];

            for (var i = 0; i < bgr.Length; i++)
                boosted[i] = Boost(bgr[i]);

            using (Mat image = Mat.FromPixelData(_height, _width, MatType.CV_8UC3, boosted))
            using (var faces = new Mat())
            {
                _detector.Detect(image, faces);

                if (faces.Empty() || faces.Rows < 1)
                    return false;

                // YuNet: x, y, w, h, ojo_der, ojo_izq, nariz, boca_der, boca_izq, score
                byte[] raw = faces.ToBytes();

                var imagePoints = new Point2f[5];
                imagePoints[0] = new Point2f(FloatAt(raw, 4), FloatAt(raw, 5));
                imagePoints[1] = new Point2f(FloatAt(raw, 6), FloatAt(raw, 7));
                imagePoints[2] = new Point2f(FloatAt(raw, 8), FloatAt(raw, 9));
                imagePoints[3] = new Point2f(FloatAt(raw, 10), FloatAt(raw, 11));
                imagePoints[4] = new Point2f(FloatAt(raw, 12), FloatAt(raw, 13));

                var rvec = new double[3];
                var tvec = new double[3];

                Cv2.SolvePnP(
                    ModelPoints,
                    imagePoints,
                    _camera,
                    _distortion,
                    ref rvec,
                    ref tvec,
                    false,
                    SolvePnPMethod.Iterative);

                double[,] rotation = RodriguesToMatrix(rvec);

                double r00 = rotation[0, 0];
                double r10 = rotation[1, 0];
                double r20 = rotation[2, 0];
                double r21 = rotation[2, 1];
                double r22 = rotation[2, 2];

                pitch = Math.Asin(-Clamp(r20, -1.0, 1.0)) * 180.0 / Math.PI;
                yaw = Math.Atan2(r10, r00) * 180.0 / Math.PI;
                roll = Math.Atan2(r21, r22) * 180.0 / Math.PI;

                // El modelo está en milímetros: se pasa a centímetros.
                x = tvec[0] / 10.0;
                y = tvec[1] / 10.0;
                z = tvec[2] / 10.0;

                return true;
            }
        }

        /// <summary>Rodrigues de toda la vida, para no depender de la sobrecarga de OpenCV.</summary>
        private static double[,] RodriguesToMatrix(double[] rotationVector)
        {
            double rx = rotationVector[0];
            double ry = rotationVector[1];
            double rz = rotationVector[2];

            double theta = Math.Sqrt(rx * rx + ry * ry + rz * rz);

            if (theta < 1e-9)
            {
                return new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            }

            double kx = rx / theta;
            double ky = ry / theta;
            double kz = rz / theta;

            double cos = Math.Cos(theta);
            double sin = Math.Sin(theta);
            double v = 1.0 - cos;

            var matrix = new double[3, 3];

            matrix[0, 0] = cos + kx * kx * v;
            matrix[0, 1] = kx * ky * v - kz * sin;
            matrix[0, 2] = kx * kz * v + ky * sin;

            matrix[1, 0] = ky * kx * v + kz * sin;
            matrix[1, 1] = cos + ky * ky * v;
            matrix[1, 2] = ky * kz * v - kx * sin;

            matrix[2, 0] = kz * kx * v - ky * sin;
            matrix[2, 1] = kz * ky * v + kx * sin;
            matrix[2, 2] = cos + kz * kz * v;

            return matrix;
        }

        private static float FloatAt(byte[] raw, int index)
        {
            return BitConverter.ToSingle(raw, index * 4);
        }

        private static byte Boost(byte value)
        {
            double normalized = value / 255.0;
            double boosted = Math.Pow(normalized, 0.55) * 255.0;

            if (boosted > 255) boosted = 255;
            if (boosted < 0) boosted = 0;

            return (byte)boosted;
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        public void Dispose()
        {
            _detector.Dispose();
        }
    }
}
