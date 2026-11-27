using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.Kinect;
using Microsoft.Kinect.Toolkit.FaceTracking;

namespace Simtrack.Kinect
{
    /// <summary>
    /// Puente Kinect v1 -> Simtrack Bridge.
    ///
    /// Posicion: esqueleto (cm). Rotacion: Face Tracking SDK (yaw/pitch/roll),
    /// con el hueso de la cabeza como respaldo.
    /// Envia 48 bytes (6 double) por UDP 4246: X, Y, Z, Yaw, Pitch, Roll.
    /// </summary>
    internal static class KinectHost
    {
        private const int PosePort = 4246;

        private static readonly UdpClient Sender = new UdpClient();
        private static readonly IPEndPoint Target = new IPEndPoint(IPAddress.Loopback, PosePort);

        private static KinectSensor _sensor;
        private static FaceTracker _faceTracker;

        private static readonly ColorImageFormat ColorFormat = ColorImageFormat.RgbResolution640x480Fps30;
        private static readonly DepthImageFormat DepthFormat = DepthImageFormat.Resolution640x480Fps30;

        private static byte[] _colorPixels;
        private static short[] _depthPixels;
        private static bool _haveColor;
        private static bool _haveDepth;

        private static Skeleton _skeleton;
        private static readonly object Gate = new object();

        private static long _frames;
        private static long _poses;
        private static long _faceFrames;
        private static long _faceFailures;
        private static long _positionOnly;
        private static long _roiNull;
        private static long _faceTries;
        private static DateTime _lastReport = DateTime.UtcNow;
        private static DateTime _lastPose = DateTime.MinValue;
        private static DateTime _lastFaceOk = DateTime.MinValue;

        private static bool _verbose = true;
        private static bool _faceEnabled = true;
        private static bool _snapshotDone;
        private static string _snapshotPath;
        private static int? _setAngle;
        private static string _depthPath;
        private static bool _depthDone;
        private static long _depthFrames;
        private static string _mode = "seated";
        private static string _logPath;
        private static bool _motorCalibrate;
        private static int _motorScore;
        private static bool _motorCollect;

        private static double _baseYaw;
        private static double _basePitch;
        private static double _baseRoll;
        private static bool _hasRotationBase;
        private static double _baseRotationYaw;
        private static double _baseRotationPitch;
        private static double _baseRotationRoll;
        private static bool _hasBoneBase;
        private static DateTime _lastRoiLog = DateTime.MinValue;
        private static DateTime _lastSkeletonAt = DateTime.MinValue;
        private static double[] _baseBoneMatrix;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            if (args != null && args.Length > 0)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    string arg = args[i];

                    if (arg == "--quiet")
                        _verbose = false;

                    if (arg == "--motor-calibrate")
                        _motorCalibrate = true;

                    if (arg == "--no-face")
                        _faceEnabled = false;

                    if (arg.StartsWith("--snapshot="))
                        _snapshotPath = arg.Substring("--snapshot=".Length);

                    if (arg.StartsWith("--depth="))
                        _depthPath = arg.Substring("--depth=".Length);

                    if (arg.StartsWith("--log="))
                        _logPath = arg.Substring("--log=".Length);

                    if (arg.StartsWith("--mode="))
                        _mode = arg.Substring("--mode=".Length);

                    if (arg.StartsWith("--set-angle="))
                        _setAngle = int.Parse(arg.Substring("--set-angle=".Length));
                }
            }

            if (_logPath != null)
            {
                try
                {
                    var writer = new StreamWriter(_logPath, false, System.Text.Encoding.UTF8);
                    writer.AutoFlush = true;
                    Console.SetOut(writer);
                }
                catch
                {
                }
            }

            Console.WriteLine("Simtrack Kinect host v0.3 (SDK 1.8 + Face Tracking)");

            var sensors = KinectSensor.KinectSensors;

            if (sensors == null || sensors.Count == 0)
            {
                Console.WriteLine("No hay ningun sensor Kinect conectado.");
                return 1;
            }

            _sensor = sensors[0];
            Console.WriteLine("Sensor: " + _sensor.UniqueKinectId);
            Console.WriteLine("Estado inicial: " + _sensor.Status);

            if (_setAngle.HasValue)
                return SetElevation(_setAngle.Value);

            if (_motorCalibrate)
                return MotorCalibrate();

            _sensor.SkeletonStream.TrackingMode = _mode == "standing"
                ? SkeletonTrackingMode.Default
                : SkeletonTrackingMode.Seated;

            _sensor.SkeletonStream.Enable(new TransformSmoothParameters
            {
                Smoothing = 0.5f,
                Correction = 0.5f,
                Prediction = 0.5f,
                JitterRadius = 0.05f,
                MaxDeviationRadius = 0.04f
            });

            Console.WriteLine("TrackingMode=" + _sensor.SkeletonStream.TrackingMode);

            _sensor.SkeletonFrameReady += OnSkeletonFrame;

            if (_faceEnabled)
            {
                _sensor.ColorStream.Enable(ColorFormat);
                _sensor.DepthStream.Enable(DepthFormat);

                _sensor.ColorFrameReady += OnColorFrame;
                _sensor.DepthFrameReady += OnDepthFrame;
            }

            if (_snapshotPath != null && !_faceEnabled)
            {
                _sensor.ColorStream.Enable(ColorFormat);
                _sensor.ColorFrameReady += OnColorFrame;
            }

            if (_depthPath != null && !_faceEnabled)
            {
                _sensor.DepthStream.Enable(DepthFormat);
                _sensor.DepthFrameReady += OnDepthFrame;
            }

            _sensor.Start();
            Console.WriteLine("Kinect iniciado. Enviando pose a 127.0.0.1:" + PosePort + ". Ctrl+C para salir.");

            if (_faceEnabled)
            {
                try
                {
                    System.Threading.Thread.Sleep(500);
                    _faceTracker = new FaceTracker(_sensor);
                    Console.WriteLine("Face Tracking: activado.");
                }
                catch (Exception ex)
                {
                    _faceTracker = null;
                    Console.WriteLine("Face Tracking no disponible: " + ex.Message);
                }
            }

            while (true)
            {
                System.Threading.Thread.Sleep(1000);
                Report();
            }
        }

        private static void Report()
        {
            double seconds = (DateTime.UtcNow - _lastReport).TotalSeconds;

            if (seconds <= 0)
                return;

            Console.WriteLine(string.Format(
                "estado={0} frames={1} poses={2} cara={3} fallos={4} soloPos={6} roiNull={7} tries={8} ({5} Hz)",
                _sensor == null ? "?" : _sensor.Status.ToString(),
                _frames,
                _poses,
                _faceFrames,
                _faceFailures,
                (int)(_poses / seconds),
                _positionOnly,
                _roiNull,
                _faceTries));

            _lastReport = DateTime.UtcNow;
            _frames = 0;
            _poses = 0;
            _faceFrames = 0;
            _faceFailures = 0;
            _positionOnly = 0;
            _roiNull = 0;
            _faceTries = 0;
        }

        // MARK: esqueleto

        private static void OnSkeletonFrame(object sender, SkeletonFrameReadyEventArgs e)
        {
            using (SkeletonFrame frame = e.OpenSkeletonFrame())
            {
                if (frame == null)
                    return;

                _frames++;

                var skeletons = new Skeleton[frame.SkeletonArrayLength];
                frame.CopySkeletonDataTo(skeletons);

                Skeleton found = null;

                foreach (Skeleton candidate in skeletons)
                {
                    if (candidate.TrackingState == SkeletonTrackingState.Tracked)
                    {
                        found = candidate;
                        break;
                    }

                    if (candidate.TrackingState == SkeletonTrackingState.PositionOnly)
                        _positionOnly++;
                }

                if (found == null)
                    return;

                if (_motorCollect)
                    _motorScore++;

                lock (Gate)
                {
                    _skeleton = found;
                }

                _lastSkeletonAt = DateTime.UtcNow;

                Joint head = found.Joints[JointType.Head];

                if (head.TrackingState == JointTrackingState.NotTracked)
                    return;

                // Sin cara (o si falla), se usa el hueso de la cabeza como respaldo.
                if (_faceTracker == null || (DateTime.UtcNow - _lastFaceOk).TotalMilliseconds > 1000)
                {
                    double yaw;
                    double pitch;
                    double roll;
                    BoneRotation(found, out yaw, out pitch, out roll);

                    SendPose(head, yaw, pitch, roll);
                }
            }
        }

        // MARK: color / profundidad

        private static void OnColorFrame(object sender, ColorImageFrameReadyEventArgs e)
        {
            using (ColorImageFrame frame = e.OpenColorImageFrame())
            {
                if (frame == null)
                    return;

                if (_colorPixels == null || _colorPixels.Length != frame.PixelDataLength)
                    _colorPixels = new byte[frame.PixelDataLength];

                frame.CopyPixelDataTo(_colorPixels);
                _haveColor = true;

                if (_snapshotPath != null && !_snapshotDone)
                    SaveColor(frame, _colorPixels);

            }
        }

        private static void OnDepthFrame(object sender, DepthImageFrameReadyEventArgs e)
        {
            using (DepthImageFrame frame = e.OpenDepthImageFrame())
            {
                if (frame == null)
                    return;

                _depthFrames++;

                if (_depthPixels == null || _depthPixels.Length != frame.PixelDataLength)
                    _depthPixels = new short[frame.PixelDataLength];

                frame.CopyPixelDataTo(_depthPixels);
                _haveDepth = true;

                if (_facePathPending())
                    SaveDepth(frame, _depthPixels);

                if (_faceTracker != null)
                    ProcessFace();
            }
        }

        private static bool _facePathPending()
        {
            return _depthPath != null && !_depthDone;
        }

        // MARK: face tracking

        private static void ProcessFace()
        {
            if (_faceTracker == null || !_haveColor || !_haveDepth)
                return;

            Skeleton skeleton;

            lock (Gate)
            {
                skeleton = _skeleton;
            }

            // Un esqueleto viejo no vale: si hace mas de 1,5 s que no se ve,
            // se pasa al modo de region de profundidad.
            if (skeleton != null && (DateTime.UtcNow - _lastSkeletonAt).TotalSeconds > 1.5)
                skeleton = null;

            if (skeleton == null)
            {
                ProcessFaceByRegion();
                return;
            }

            try
            {
                FaceTrackFrame frame = _faceTracker.Track(ColorFormat, _colorPixels, DepthFormat, _depthPixels, skeleton);

                if (frame == null)
                {
                    _faceFailures++;
                    return;
                }

                using (frame)
                {
                    if (!frame.TrackSuccessful)
                    {
                        _faceFailures++;
                        return;
                    }

                    _faceFrames++;
                    _lastFaceOk = DateTime.UtcNow;

                    Vector3DF rotation = frame.Rotation;

                    double pitch = rotation.X * 180.0 / Math.PI;
                    double yaw = rotation.Y * 180.0 / Math.PI;
                    double roll = rotation.Z * 180.0 / Math.PI;

                    Joint head = skeleton.Joints[JointType.Head];
                    SendPose(head, yaw, pitch, roll);
                }
            }
            catch (Exception)
            {
                // El FaceTracker nativo puede lanzar si no soporta el sensor;
                // se cuenta como fallo y se usa el respaldo del hueso.
                _faceFailures++;
                _lastFaceOk = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Sin esqueleto (solo se ve del esternon para arriba) se localiza la
        /// cabeza en la profundidad y se le pasa esa zona al FaceTracker.
        /// </summary>
        private static void ProcessFaceByRegion()
        {
            int width = 640;
            int height = 480;

            Rect? roi = ComputeHeadRegion(width, height);

            if (roi == null)
            {
                _roiNull++;
                return;
            }

            _faceTries++;

            if ((DateTime.UtcNow - _lastRoiLog).TotalSeconds > 1.5)
            {
                _lastRoiLog = DateTime.UtcNow;
                Console.WriteLine(string.Format("ROI cara x={0} y={1} w={2} h={3}", roi.Value.X, roi.Value.Y, roi.Value.Width, roi.Value.Height));
            }

            try
            {
                FaceTrackFrame frame = _faceTracker.Track(ColorFormat, _colorPixels, DepthFormat, _depthPixels, roi.Value);

                if (frame == null)
                {
                    _faceFailures++;
                    return;
                }

                using (frame)
                {
                    if (!frame.TrackSuccessful)
                    {
                        _faceFailures++;
                        return;
                    }

                    _faceFrames++;
                    _lastFaceOk = DateTime.UtcNow;

                    Vector3DF rotation = frame.Rotation;
                    Vector3DF translation = frame.Translation;

                    double pitch = rotation.X * 180.0 / Math.PI;
                    double yaw = rotation.Y * 180.0 / Math.PI;
                    double roll = rotation.Z * 180.0 / Math.PI;

                    SendPoseAt(translation.X * 100.0, translation.Y * 100.0, translation.Z * 100.0, yaw, pitch, roll);
                }
            }
            catch (Exception)
            {
                _faceFailures++;
                _lastFaceOk = DateTime.MinValue;
            }
        }

        private static Rect? ComputeHeadRegion(int width, int height)
        {
            short[] depth = _depthPixels;

            if (depth == null || depth.Length < width * height)
                return null;

            int minX = width, maxX = -1, minY = height, maxY = -1;

            for (var y = 0; y < height; y += 2)
            {
                for (var x = 0; x < width; x += 2)
                {
                    int millimeters = depth[y * width + x] >> 3;

                    if (millimeters < 450 || millimeters > 2600)
                        continue;

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0 || maxY < 0)
                return null;

            int bodyWidth = maxX - minX;
            int bodyHeight = maxY - minY;

            if (bodyHeight < 40 || bodyWidth < 40)
                return null;

            int centerX = (minX + maxX) / 2;
            int headWidth = Math.Max(90, (int)(bodyWidth * 0.55));
            int headHeight = Math.Max(70, (int)(bodyHeight * 0.45));

            int x0 = Math.Max(0, centerX - headWidth / 2);
            int y0 = Math.Max(0, minY - 15);

            headWidth = Math.Min(headWidth, width - x0);
            headHeight = Math.Min(headHeight, height - y0);

            if (headWidth < 40 || headHeight < 40)
                return null;

            return new Rect(x0, y0, headWidth, headHeight);
        }
        private static void BoneRotation(Skeleton skeleton, out double yaw, out double pitch, out double roll)
        {
            yaw = 0;
            pitch = 0;
            roll = 0;

            BoneOrientationCollection orientations = skeleton.BoneOrientations;

            if (orientations == null || orientations.Count == 0)
                return;

            Matrix4 rotation = orientations[JointType.Head].AbsoluteRotation.Matrix;

            var current = new double[]
            {
                rotation.M11, rotation.M12, rotation.M13,
                rotation.M21, rotation.M22, rotation.M23,
                rotation.M31, rotation.M32, rotation.M33
            };

            if (_baseBoneMatrix == null)
                _baseBoneMatrix = current;

            var relative = new double[9];

            for (var row = 0; row < 3; row++)
            {
                for (var col = 0; col < 3; col++)
                {
                    double sum = 0;

                    for (var k = 0; k < 3; k++)
                        sum += _baseBoneMatrix[k * 3 + row] * current[k * 3 + col];

                    relative[row * 3 + col] = sum;
                }
            }

            yaw = Math.Atan2(relative[3], relative[0]) * 180.0 / Math.PI;
            pitch = Math.Asin(Clamp(-relative[6], -1, 1)) * 180.0 / Math.PI;
            roll = Math.Atan2(relative[7], relative[8]) * 180.0 / Math.PI;
        }

        // MARK: envio

        private static void SendPose(Joint head, double yaw, double pitch, double roll)
        {
            SendPoseAt(head.Position.X * 100.0, head.Position.Y * 100.0, head.Position.Z * 100.0, yaw, pitch, roll);
        }

        private static void SendPoseAt(double x, double y, double z, double yaw, double pitch, double roll)
        {
            // Primera pose = cero de rotacion (para que el juego arranque centrado).
            if (!_hasRotationBase)
            {
                _baseYaw = yaw;
                _basePitch = pitch;
                _baseRoll = roll;
                _hasRotationBase = true;
            }

            double outYaw = Normalize(yaw - _baseYaw);
            double outPitch = Normalize(pitch - _basePitch);
            double outRoll = Normalize(roll - _baseRoll);

            // Sin cara fiable se usa el hueso, que ya viene relativo.
            if (_faceTracker == null || (DateTime.UtcNow - _lastFaceOk).TotalMilliseconds > 1000)
            {
                outYaw = yaw;
                outPitch = pitch;
                outRoll = roll;
            }

            var data = new byte[48];
            var values = new double[] { x, y, z, outYaw, outPitch, outRoll };

            for (var i = 0; i < values.Length; i++)
            {
                byte[] bytes = BitConverter.GetBytes(values[i]);

                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);

                Buffer.BlockCopy(bytes, 0, data, i * 8, 8);
            }

            try
            {
                Sender.Send(data, data.Length, Target);
                _poses++;
                _lastPose = DateTime.UtcNow;

                if (_verbose && (_poses % 30) == 0)
                {
                    Console.WriteLine(string.Format(
                        "pose X={0,6:F1} Y={1,6:F1} Z={2,6:F1} yaw={3,6:F1} pitch={4,6:F1} roll={5,6:F1}",
                        x, y, z, outYaw, outPitch, outRoll));
                }
            }
            catch (SocketException)
            {
            }
        }

        private static double Normalize(double angle)
        {
            while (angle > 180) angle -= 360;
            while (angle < -180) angle += 360;
            return angle;
        }

        // MARK: utilidades

        private static void SaveColor(ColorImageFrame frame, byte[] pixels)
        {
            try
            {
                int width = frame.Width;
                int height = frame.Height;

                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb))
                {
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    var line = new byte[width * 4];

                    try
                    {
                        for (var y2 = 0; y2 < height; y2++)
                        {
                            Buffer.BlockCopy(pixels, y2 * width * 4, line, 0, width * 4);

                            for (var x2 = 0; x2 < width; x2++)
                            {
                                byte r = line[x2 * 4];
                                line[x2 * 4] = line[x2 * 4 + 2];
                                line[x2 * 4 + 2] = r;
                            }

                            IntPtr destination = new IntPtr(data.Scan0.ToInt64() + (long)y2 * data.Stride);
                            System.Runtime.InteropServices.Marshal.Copy(line, 0, destination, line.Length);
                        }
                    }
                    finally
                    {
                        bitmap.UnlockBits(data);
                    }

                    bitmap.Save(_snapshotPath, ImageFormat.Png);
                    _snapshotDone = true;
                    Console.WriteLine("Snapshot guardado en " + _snapshotPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("No se pudo guardar la imagen: " + ex.Message);
                _snapshotDone = true;
            }
        }

        private static void SaveDepth(DepthImageFrame frame, short[] pixels)
        {
            try
            {
                int width = frame.Width;
                int height = frame.Height;

                using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                    var line = new byte[width * 3];

                    try
                    {
                        for (var y2 = 0; y2 < height; y2++)
                        {
                            for (var x2 = 0; x2 < width; x2++)
                            {
                                int millimeters = pixels[y2 * width + x2] >> 3;
                                byte gray = 0;

                                if (millimeters > 0)
                                {
                                    int value = 255 - (millimeters - 400) * 255 / 2600;
                                    if (value < 0) value = 0;
                                    if (value > 255) value = 255;
                                    gray = (byte)value;
                                }

                                line[x2 * 3] = gray;
                                line[x2 * 3 + 1] = gray;
                                line[x2 * 3 + 2] = gray;
                            }

                            IntPtr destination = new IntPtr(data.Scan0.ToInt64() + (long)y2 * data.Stride);
                            System.Runtime.InteropServices.Marshal.Copy(line, 0, destination, line.Length);
                        }
                    }
                    finally
                    {
                        bitmap.UnlockBits(data);
                    }

                    bitmap.Save(_depthPath, ImageFormat.Png);
                    _depthDone = true;
                    Console.WriteLine("Profundidad guardada en " + _depthPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("No se pudo guardar la profundidad: " + ex.Message);
                _depthDone = true;
            }
        }

        private static int MotorCalibrate()
        {
            Console.WriteLine("Calibrando motor del Kinect (barrido de inclinacion)...");

            _sensor.SkeletonStream.TrackingMode = SkeletonTrackingMode.Seated;
            _sensor.SkeletonStream.Enable();

            try
            {
                _sensor.Start();
                System.Threading.Thread.Sleep(1800);
            }
            catch (Exception ex)
            {
                Console.WriteLine("No se pudo arrancar el sensor: " + ex.Message);
                return 3;
            }

            int startAngle = 0;

            try
            {
                startAngle = _sensor.ElevationAngle;
            }
            catch
            {
            }

            int[] angles = { -25, -20, -15, -10, -5, 0, 5, 10, 15, 20 };
            int bestAngle = startAngle;
            int bestScore = -1;

            foreach (int angle in angles)
            {
                try
                {
                    _sensor.ElevationAngle = angle;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Angulo " + angle + ": no se pudo mover (" + ex.Message + ")");
                    continue;
                }

                System.Threading.Thread.Sleep(1100);

                _motorScore = 0;
                _motorCollect = true;
                System.Threading.Thread.Sleep(1100);
                _motorCollect = false;

                Console.WriteLine("Angulo " + angle + ": esqueletos " + _motorScore);

                if (_motorScore > bestScore)
                {
                    bestScore = _motorScore;
                    bestAngle = angle;
                }
            }

            try
            {
                // Si no ha visto a nadie, se deja el sensor como estaba.
                _sensor.ElevationAngle = bestScore > 0 ? bestAngle : startAngle;
                System.Threading.Thread.Sleep(900);
            }
            catch
            {
            }

            Console.WriteLine("MOTOR_OK angulo=" + bestAngle + " esqueletos=" + bestScore);

            try
            {
                _sensor.Stop();
            }
            catch
            {
            }

            return bestScore > 0 ? 0 : 2;
        }
        private static int SetElevation(int degrees)
        {
            int target = Math.Max(-27, Math.Min(27, degrees));

            try
            {
                _sensor.Start();
                System.Threading.Thread.Sleep(1500);
                _sensor.ElevationAngle = target;
                System.Threading.Thread.Sleep(1200);
                Console.WriteLine("Angulo de inclinacion: " + _sensor.ElevationAngle + " grados (pedido " + target + ").");
                _sensor.Stop();
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("No se pudo ajustar el motor: " + ex.Message);
                return 2;
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
