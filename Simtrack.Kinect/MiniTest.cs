using System;
using OpenCvSharp;

internal static class Mini
{
    private static void Main(string[] args)
    {
        using (var cascade = new CascadeClassifier(args[1]))
        using (Mat color = Cv2.ImRead(args[0], ImreadModes.Color))
        {
            Console.WriteLine("imagen " + color.Width + "x" + color.Height + " canales " + color.Channels());
            Console.WriteLine("brillo original: " + Cv2.Mean(color).Val0.ToString("F0"));

            byte[] bytes = color.ToBytes();
            Console.WriteLine("bytes: " + bytes.Length);

            using (var copy = new Mat(color.Height, color.Width, MatType.CV_8UC3))
            {
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, copy.Data, bytes.Length);
                Console.WriteLine("brillo copia: " + Cv2.Mean(copy).Val0.ToString("F0"));

                using (var gray = new Mat())
                {
                    Cv2.CvtColor(copy, gray, ColorConversionCodes.BGR2GRAY);
                    Cv2.EqualizeHist(gray, gray);
                    Console.WriteLine("brillo gris: " + Cv2.Mean(gray).Val0.ToString("F0"));

                    Rect[] faces = cascade.DetectMultiScale(gray, 1.1, 3, HaarDetectionTypes.ScaleImage, new Size(40, 40));
                    Console.WriteLine("caras: " + (faces == null ? 0 : faces.Length));
                }
            }
        }
    }
}