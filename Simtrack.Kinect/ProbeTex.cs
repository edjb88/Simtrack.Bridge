using System;
using System.Runtime.InteropServices;

internal static class ProbeTex
{
    [DllImport("Kinect10.dll")]
    private static extern int NuiGetSensorCount(out int pCount);

    [DllImport("Kinect10.dll")]
    private static extern int NuiCreateSensorByIndex(int index, out IntPtr ppSensor);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitDelegate(IntPtr self, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ShutdownDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenDelegate(IntPtr self, int type, int res, uint flags, uint limit, IntPtr ev, out IntPtr stream);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFrameDelegate(IntPtr self, IntPtr stream, uint wait, [In, Out] byte[] frame);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LockDelegate(IntPtr self, uint level, [In, Out] byte[] lockedRect, IntPtr rect, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DescDelegate(IntPtr self, uint level, [In, Out] byte[] desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CountDelegate(IntPtr self, [In, Out] byte[] count);

    private static void Main()
    {
        IntPtr sensor;
        NuiCreateSensorByIndex(0, out sensor);
        IntPtr vt = Marshal.ReadIntPtr(sensor);

        var init = (InitDelegate)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, 3 * IntPtr.Size), typeof(InitDelegate));
        var open = (OpenDelegate)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, 6 * IntPtr.Size), typeof(OpenDelegate));
        var getFrame = (GetFrameDelegate)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, 9 * IntPtr.Size), typeof(GetFrameDelegate));
        var release = (GetFrameDelegate)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, 10 * IntPtr.Size), typeof(GetFrameDelegate));

        Console.WriteLine("init=" + init(sensor, 2));

        IntPtr stream;
        Console.WriteLine("open=" + open(sensor, 5, 2, 0, 2, IntPtr.Zero, out stream));

        var frame = new byte[128];
        Console.WriteLine("get=" + getFrame(sensor, stream, 1000, frame));

        IntPtr texture = Marshal.ReadIntPtr(frame, 24);
        Console.WriteLine("texture=0x" + texture.ToString("X"));

        if (texture == IntPtr.Zero)
            return;

        IntPtr tvt = Marshal.ReadIntPtr(texture);

        for (var slot = 3; slot <= 8; slot++)
        {
            IntPtr fn = Marshal.ReadIntPtr(tvt, slot * IntPtr.Size);

            try
            {
                var desc = new byte[8];
                var descFn = (DescDelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(DescDelegate));
                int hr = descFn(texture, 0, desc);

                Console.WriteLine(string.Format(
                    "slot {0}: hr=0x{1:X8} w={2} h={3}",
                    slot, hr, BitConverter.ToUInt32(desc, 0), BitConverter.ToUInt32(desc, 4)));
            }
            catch (Exception ex)
            {
                Console.WriteLine("slot " + slot + ": " + ex.Message);
            }
        }

        // Prueba de LockRect en los slots 3..5 con NUI_LOCKED_RECT
        for (var slot = 3; slot <= 5; slot++)
        {
            IntPtr fn = Marshal.ReadIntPtr(tvt, slot * IntPtr.Size);
            var lockFn = (LockDelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(LockDelegate));
            var rect = new byte[16];

            int hr = lockFn(texture, 0, rect, IntPtr.Zero, 0);

            Console.WriteLine(string.Format(
                "lock slot {0}: hr=0x{1:X8} pitch={2} size={3}",
                slot, hr, BitConverter.ToInt32(rect, 0), BitConverter.ToInt32(rect, 4)));
        }

        release(sensor, stream, 0, frame);
    }
}
