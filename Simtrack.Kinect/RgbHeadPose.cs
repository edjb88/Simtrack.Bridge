using System;
using OpenCvSharp;
using OpenCvSharp.Face;

namespace Simtrack.Kinect
{
    /// <summary>
    /// Head pose a partir de imagen (RGB o infrarrojos del Kinect), sin esqueleto.
    /// Haar + Facemark LBF (68 puntos) + solvePnP.
    /// </summary>
    internal sealed class RgbHeadPose : IDisposable
    {
        private readonly CascadeClassifier _cascade;
        private readonly FacemarkLBF _facemark;
        private readonly int _width;
        private readonly int _height;
        private readonly byte[] _bgr;
        private readonly double[,] _camera;
        private readonly double[] _distortion;

        public static bool Debug;

        public double LastCenterX { get; private set; }
        public double LastCenterY { get; private set; }
        public double LastFaceWidth { get; private set; }

        // Convencion OpenCV: X derecha, Y abajo, Z hacia el fondo.
        private static readonly Point3f[] ModelPoints =
        {
            new Point3f(0f, 0f, 0f),           // 30 nariz
            new Point3f(-225f, -170f, 135f),   // 36 ojo
            new Point3f(225f, -170f, 135f),    // 45 ojo
            new Point3f(-150f, 150f, 125f),    // 48 boca
            new Point3f(150f, 150f, 125f),     // 54 boca
            new Point3f(0f, 330f, 65f)         // 8 barbilla
        };

        public RgbHeadPose(string cascadePath, string facemarkPath, int width, int height)
        {
            _width = width;
            _height = height;
            _bgr = new byte[width * height * 3];

            _cascade = new CascadeClassifier(cascadePath);
            _facemark = FacemarkLBF.Create(new FacemarkLBF.Params());
            _facemark.LoadModel(facemarkPath);

            const double focal = 533.0;

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

        public bool TryEstimateBgr(byte[] bgr, out double x, out double y, out double z, out double yaw, out double pitch, out double roll)
        {
            x = 0;
            y = 0;
            z = 0;
            yaw = 0;
            pitch = 0;
            roll = 0;

            if (bgr == null || bgr.Length < _width * _height * 3)
                return false;

            var boosted = new byte[bgr.Length];

            for (var i = 0; i < bgr.Length; i++)
                boosted[i] = Boost(bgr[i]);

            using (var color = new Mat(_height, _width, MatType.CV_8UC3))
            using (var gray = new Mat())
            {
                System.Runtime.InteropServices.Marshal.Copy(boosted, 0, color.Data, boosted.Length);
                Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.EqualizeHist(gray, gray);

                return RunDetection(gray, out x, out y, out z, out yaw, out pitch, out roll);
            }
        }

        /// <summary>Igual pero con una imagen en gris (infrarrojos del Kinect).</summary>
        public bool TryEstimateGray(byte[] gray8, out double x, out double y, out double z, out double yaw, out double pitch, out double roll)
        {
            x = 0;
            y = 0;
            z = 0;
            yaw = 0;
            pitch = 0;
            roll = 0;

            if (gray8 == null || gray8.Length < _width * _height)
                return false;

            using (var gray = new Mat(_height, _width, MatType.CV_8UC1))
            {
                System.Runtime.InteropServices.Marshal.Copy(gray8, 0, gray.Data, _width * _height);
                Cv2.EqualizeHist(gray, gray);

                return RunDetection(gray, out x, out y, out z, out yaw, out pitch, out roll);
            }
        }

        private bool RunDetection(Mat gray, out double x, out double y, out double z, out double yaw, out double pitch, out double roll)
        {
            x = 0;
            y = 0;
            z = 0;
            yaw = 0;
            pitch = 0;
            roll = 0;

            Rect[] faces = _cascade.DetectMultiScale(
                gray,
                1.1,
                3,
                HaarDetectionTypes.ScaleImage,
                new Size(40, 40));

            if (Debug)
                Console.WriteLine("Haar caras: " + (faces == null ? 0 : faces.Length));

            if (faces == null || faces.Length == 0)
                return false;

            Rect face = faces[0];
            LastCenterX = face.X + face.Width / 2.0;
            LastCenterY = face.Y + face.Height / 2.0;
            LastFaceWidth = face.Width;

            Point2f[][] landmarks;
            bool ok;

            try
            {
                using (InputArray faceArray = InputArray.Create(faces, MatType.CV_32SC4))
                {
                    ok = _facemark.Fit(gray, faceArray, out landmarks);
                }
            }
            catch (Exception)
            {
                return false;
            }

            if (!ok || landmarks == null || landmarks.Length == 0 || landmarks[0] == null || landmarks[0].Length < 68)
                return false;

            Point2f[] points = landmarks[0];

            var imagePoints = new Point2f[6];
            imagePoints[0] = points[30];
            imagePoints[1] = points[36];
            imagePoints[2] = points[45];
            imagePoints[3] = points[48];
            imagePoints[4] = points[54];
            imagePoints[5] = points[8];

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
            double r02 = rotation[0, 2];
            double r10 = rotation[1, 0];
            double r12 = rotation[1, 2];
            double r22 = rotation[2, 2];

            yaw = Math.Atan2(r02, r22) * 180.0 / Math.PI;
            pitch = Math.Asin(-Clamp(r12, -1.0, 1.0)) * 180.0 / Math.PI;
            roll = Math.Atan2(r10, r00) * 180.0 / Math.PI;

            x = tvec[0] / 10.0;
            y = tvec[1] / 10.0;
            z = tvec[2] / 10.0;

            return true;
        }

        private static double[,] RodriguesToMatrix(double[] rotationVector)
        {
            double rx = rotationVector[0];
            double ry = rotationVector[1];
            double rz = rotationVector[2];

            double theta = Math.Sqrt(rx * rx + ry * ry + rz * rz);

            if (theta < 1e-9)
                return new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

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
            _cascade.Dispose();
            _facemark.Dispose();
        }
    }
}
