using System;
using OpenCvSharp;
using Simtrack.Kinect;

internal static class TestRgb
{
    private static void Main(string[] args)
    {
        RgbHeadPose.Debug = true;

        foreach (string path in args)
        {
            if (!System.IO.File.Exists(path)) continue;

            using (Mat mat = Cv2.ImRead(path, ImreadModes.Color))
            {
                if (mat.Empty()) continue;

                Console.WriteLine("--- " + System.IO.Path.GetFileName(path) + " " + mat.Width + "x" + mat.Height);
                int length = mat.Rows * mat.Cols * mat.Channels();
                byte[] bgr = new byte[length];
                System.Runtime.InteropServices.Marshal.Copy(mat.Data, bgr, 0, length);

                using (var rgb = new RgbHeadPose("haarcascade_frontalface_default.xml", "lbfmodel.yaml", mat.Width, mat.Height))
                {
                    double x, y, z, yaw, pitch, roll;
                    bool ok = rgb.TryEstimateBgr(bgr, out x, out y, out z, out yaw, out pitch, out roll);
                    Console.WriteLine("deteccion: " + ok);
                    if (ok) Console.WriteLine(string.Format("X={0:F1} Y={1:F1} Z={2:F1} yaw={3:F1} pitch={4:F1} roll={5:F1}", x, y, z, yaw, pitch, roll));
                }
            }
        }
    }
}