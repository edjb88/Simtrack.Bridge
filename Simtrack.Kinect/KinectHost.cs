using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.Kinect;

namespace Simtrack.Kinect
{
    /// <summary>
    /// Puente Kinect v1 -> Simtrack Bridge.
    /// Lee el esqueleto y la orientacion del hueso de la cabeza y manda la pose
    /// por UDP al puerto 4246, en el mismo formato de 48 bytes que el iPhone:
    /// 6 double little-endian = X, Y, Z (cm) + Yaw, Pitch, Roll (grados).
    /// </summary>
    internal static class KinectHost
    {
        private const int PosePort = 4246;

        private static readonly UdpClient Sender = new UdpClient();
        private static readonly IPEndPoint Target = new IPEndPoint(IPAddress.Loopback, PosePort);

        private static KinectSensor _sensor;
        private static long _frames;
        private static long _poses;
        private static long _positionOnly;
        private static DateTime _lastReport = DateTime.UtcNow;
        private static bool _verbose = true;
        private static bool _snapshotDone;
        private static string _snapshotPath;
        private static int? _setAngle;
        private static string _depthPath;
        private static string _mode = "seated";
        private static string _logPath;
        private static bool _depthDone;
        private static long _depthFrames;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            if (args != null)
            {
                foreach (string arg in args)
                {
                    if (arg == "--quiet")
                        _verbose = false;

                    if (arg.StartsWith("--snapshot="))
                        _snapshotPath = arg.Substring("--snapshot=".Length);

                    if (arg.StartsWith("--depth="))
                        _depthPath = arg.Substring("--depth=".Length);

                    if (arg.StartsWith("--log="))
                        _logPath = arg.Substring("--log=".Length);

                    if (arg.StartsWith("--mode="))
                        _mode = arg.Substring("--mode=".Length);

                    if (arg.StartsWith("--set-angle="))
                    {
                        int angle;
                        if (int.TryParse(arg.Substring("--set-angle=".Length), out angle))
                            _setAngle = angle;
                    }
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

            Console.WriteLine("Simtrack Kinect host v0.2 (Kinect SDK 1.8)");

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
            {
                return SetElevation(_setAngle.Value);
            }

            // Modo sentado: en un escritorio no se ve el cuerpo completo.
            // OJO: el "near range" solo existe en Kinect for Windows; con un
            // Kinect de Xbox 360 lanza "feature not supported", asi que se usa
            // el rango normal (0,8 - 4 m).
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

            Console.WriteLine("TrackingMode real: " + _sensor.SkeletonStream.TrackingMode);
            Console.WriteLine("DepthRange real: " + _sensor.DepthStream.Range);

            _sensor.SkeletonFrameReady += OnSkeletonFrame;

            if (_snapshotPath != null)
            {
                _sensor.ColorStream.Enable(ColorImageFormat.RgbResolution640x480Fps30);
                _sensor.ColorFrameReady += OnColorFrame;
            }

            if (_depthPath != null)
            {
                _sensor.DepthStream.Enable(DepthImageFormat.Resolution640x480Fps30);
                _sensor.DepthFrameReady += OnDepthFrame;
            }

            _sensor.Start();
            Console.WriteLine("Kinect iniciado. Enviando pose a 127.0.0.1:" + PosePort + ". Ctrl+C para salir.");

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
                "estado={0} frames={1} sentados={2} poses={3} depth={5} ({4} Hz)",
                _sensor == null ? "?" : _sensor.Status.ToString(),
                _frames,
                _positionOnly,
                _poses,
                (int)(_poses / seconds),
                _depthFrames));

            _lastReport = DateTime.UtcNow;
            _frames = 0;
            _poses = 0;
            _positionOnly = 0;
        }

        private static void OnSkeletonFrame(object sender, SkeletonFrameReadyEventArgs e)
        {
            using (SkeletonFrame frame = e.OpenSkeletonFrame())
            {
                if (frame == null)
                    return;

                _frames++;

                var skeletons = new Skeleton[frame.SkeletonArrayLength];
                frame.CopySkeletonDataTo(skeletons);

                Skeleton skeleton = null;

                foreach (Skeleton candidate in skeletons)
                {
                    if (candidate.TrackingState == SkeletonTrackingState.Tracked)
                    {
                        skeleton = candidate;
                        break;
                    }

                    if (candidate.TrackingState == SkeletonTrackingState.PositionOnly)
                        _positionOnly++;
                }

                if (skeleton == null)
                    return;

                Joint head = skeleton.Joints[JointType.Head];

                if (head.TrackingState == JointTrackingState.NotTracked)
                    return;

                // Posicion en metros (sistema del sensor) -> centimetros.
                double x = head.Position.X * 100.0;
                double y = head.Position.Y * 100.0;
                double z = head.Position.Z * 100.0;

                double yaw = 0.0;
                double pitch = 0.0;
                double roll = 0.0;

                BoneOrientationCollection orientations = skeleton.BoneOrientations;

                if (orientations != null && orientations.Count > 0)
                {
                    BoneOrientation headBone = orientations[JointType.Head];
                    Matrix4 rotation = headBone.AbsoluteRotation.Matrix;

                    // Ejes del hueso expresados en el sistema del sensor.
                    double fx = rotation.M13;
                    double fy = rotation.M23;
                    double fz = rotation.M33;

                    double ux = rotation.M12;
                    double uy = rotation.M22;

                    yaw = Math.Atan2(fx, -fz) * 180.0 / Math.PI;
                    pitch = Math.Asin(Clamp(fy, -1.0, 1.0)) * 180.0 / Math.PI;
                    roll = Math.Atan2(ux, uy) * 180.0 / Math.PI;
                }

                Send(x, y, z, yaw, pitch, roll);
                _poses++;

                if (_verbose && (_poses % 30) == 0)
                {
                    Console.WriteLine(string.Format(
                        "head X={0,6:F1} Y={1,6:F1} Z={2,6:F1} cm  yaw={3,6:F1} pitch={4,6:F1} roll={5,6:F1}",
                        x, y, z, yaw, pitch, roll));
                }
            }
        }

        private static void OnColorFrame(object sender, ColorImageFrameReadyEventArgs e)
        {
            if (_snapshotDone)
                return;

            using (ColorImageFrame frame = e.OpenColorImageFrame())
            {
                if (frame == null)
                    return;

                try
                {
                    int width = frame.Width;
                    int height = frame.Height;
                    var pixels = new byte[frame.PixelDataLength];

                    frame.CopyPixelDataTo(pixels);

                    using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb))
                    {
                        Rectangle rectangle = new Rectangle(0, 0, width, height);
                        BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);

                        try
                        {
                            var line = new byte[width * 4];

                            for (int y = 0; y < height; y++)
                            {
                                Buffer.BlockCopy(pixels, y * width * 4, line, 0, width * 4);

                                // El Kinect entrega RGBX; System.Drawing espera BGRX.
                                for (int x = 0; x < width; x++)
                                {
                                    byte r = line[x * 4];
                                    line[x * 4] = line[x * 4 + 2];
                                    line[x * 4 + 2] = r;
                                }

                                IntPtr destination = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
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

        private static void OnDepthFrame(object sender, DepthImageFrameReadyEventArgs e)
        {
            using (DepthImageFrame frame = e.OpenDepthImageFrame())
            {
                if (frame == null)
                    return;

                _depthFrames++;

                if (_depthDone)
                    return;

                try
                {
                    int width = frame.Width;
                    int height = frame.Height;
                    var raw = new short[frame.PixelDataLength];
                    frame.CopyPixelDataTo(raw);

                    short min = short.MaxValue;
                    short max = 0;

                    using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                    {
                        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

                        try
                        {
                            var line = new byte[width * 3];

                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    int millimeters = raw[y * width + x] >> 3;
                                    byte gray = 0;

                                    if (millimeters > 0)
                                    {
                                        if (millimeters < min) min = (short)millimeters;
                                        if (millimeters > max) max = (short)millimeters;

                                        int value = 255 - (millimeters - 400) * 255 / 2600;
                                        if (value < 0) value = 0;
                                        if (value > 255) value = 255;
                                        gray = (byte)value;
                                    }

                                    line[x * 3] = gray;
                                    line[x * 3 + 1] = gray;
                                    line[x * 3 + 2] = gray;
                                }

                                IntPtr destination = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                                System.Runtime.InteropServices.Marshal.Copy(line, 0, destination, line.Length);
                            }
                        }
                        finally
                        {
                            bitmap.UnlockBits(data);
                        }

                        bitmap.Save(_depthPath, ImageFormat.Png);
                        _depthDone = true;
                        Console.WriteLine("Profundidad guardada en " + _depthPath + " (min " + min + " mm, max " + max + " mm)");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("No se pudo guardar la profundidad: " + ex.Message);
                    _depthDone = true;
                }
            }
        }
        private static void Send(double x, double y, double z, double yaw, double pitch, double roll)
        {
            var data = new byte[48];
            var values = new double[] { x, y, z, yaw, pitch, roll };

            for (int i = 0; i < values.Length; i++)
            {
                byte[] bytes = BitConverter.GetBytes(values[i]);

                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);

                Buffer.BlockCopy(bytes, 0, data, i * 8, 8);
            }

            try
            {
                Sender.Send(data, data.Length, Target);
            }
            catch (SocketException)
            {
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
