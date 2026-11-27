using System;
using System.IO;
using OpenCvSharp;
using Simtrack.Kinect;

internal static class TestRgb
{
    private static void Main(string[] args)
    {
        string path = args[0];
        using (Mat mat = Cv2.ImRead(path, ImreadModes.Color))
        {
            Console.WriteLine("imagen " + mat.Width + "x" + mat.Height);
            byte[] bgr = mat.ToBytes();

            using (var rgb = new RgbHeadPose(args[1], 640, 480))
            {
                double x, y, z, yaw, pitch, roll;
                bool ok = rgb.TryEstimateBgr(bgr, out x, out y, out z, out yaw, out pitch, out roll);
                Console.WriteLine("deteccion: " + ok);
                if (ok) Console.WriteLine(string.Format("X={0:F1} Y={1:F1} Z={2:F1} yaw={3:F1} pitch={4:F1} roll={5:F1}", x, y, z, yaw, pitch, roll));
            }
        }
    }
}