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

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("Simtrack Kinect host v0.2 (Kinect SDK 1.8)");

            if (args != null)
            {
                foreach (string arg in args)
                {
                    if (arg == "--quiet")
                        _verbose = false;

                    if (arg.StartsWith("--snapshot="))
                        _snapshotPath = arg.Substring("--snapshot=".Length);
                }
            }

            var sensors = KinectSensor.KinectSensors;

            if (sensors == null || sensors.Count == 0)
            {
                Console.WriteLine("No hay ningun sensor Kinect conectado.");
                return 1;
            }

            _sensor = sensors[0];
            Console.WriteLine("Sensor: " + _sensor.UniqueKinectId);
            Console.WriteLine("Estado inicial: " + _sensor.Status);

            // Modo sentado + rango cercano: en un escritorio solo se ve medio
            // cuerpo, y con el modo normal Kinect no engancha el esqueleto.
            _sensor.DepthStream.Range = DepthRange.Near;
            _sensor.SkeletonStream.TrackingMode = SkeletonTrackingMode.Seated;
            _sensor.SkeletonStream.EnableTrackingInNearRange = true;

            _sensor.SkeletonStream.Enable(new TransformSmoothParameters
            {
                Smoothing = 0.5f,
                Correction = 0.5f,
                Prediction = 0.5f,
                JitterRadius = 0.05f,
                MaxDeviationRadius = 0.04f
            });

            _sensor.SkeletonFrameReady += OnSkeletonFrame;

            if (_snapshotPath != null)
            {
                _sensor.ColorStream.Enable(ColorImageFormat.RgbResolution640x480Fps30);
                _sensor.ColorFrameReady += OnColorFrame;
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
                "estado={0} frames={1} sentados={2} poses={3} ({4} Hz)",
                _sensor == null ? "?" : _sensor.Status.ToString(),
                _frames,
                _positionOnly,
                _poses,
                (int)(_poses / seconds)));

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
