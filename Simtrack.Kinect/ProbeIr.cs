using System;
using System.Runtime.InteropServices;

internal static class ProbeIr
{
    [DllImport("Kinect10.dll")]
    private static extern int NuiGetSensorCount(out int pCount);

    [DllImport("Kinect10.dll")]
    private static extern int NuiCreateSensorByIndex(int index, out IntPtr ppSensor);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitializeDelegate(IntPtr self, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ShutdownDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StreamOpenDelegate(IntPtr self, int imageType, int resolution, uint flags, uint limit, IntPtr nextEvent, out IntPtr stream);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr self);

    private static void Main()
    {
        int count;
        NuiGetSensorCount(out count);
        Console.WriteLine("sensores=" + count);

        int[] flagsList = { 0x1, 0x2, 0x4, 0x8, 0x10, 0x20, 0x40, 0x10000000 };
        int[] typeList = { 0, 1, 3, 4, 5, 6 };
        int[] resList = { 1, 2 };

        foreach (int flags in flagsList)
        {
            foreach (int type in typeList)
            {
                foreach (int res in resList)
                {
                    IntPtr sensor;
                    if (NuiCreateSensorByIndex(0, out sensor) < 0 || sensor == IntPtr.Zero)
                        continue;

                    IntPtr vtable = Marshal.ReadIntPtr(sensor);
                    var init = (InitializeDelegate)Marshal.GetDelegateForFunctionPointer(
                        Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size), typeof(InitializeDelegate));
                    var shutdown = (ShutdownDelegate)Marshal.GetDelegateForFunctionPointer(
                        Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size), typeof(ShutdownDelegate));
                    var open = (StreamOpenDelegate)Marshal.GetDelegateForFunctionPointer(
                        Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size), typeof(StreamOpenDelegate));
                    var release = (ReleaseDelegate)Marshal.GetDelegateForFunctionPointer(
                        Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size), typeof(ReleaseDelegate));

                    int hr = init(sensor, (uint)flags);

                    if (hr < 0)
                    {
                        release(sensor);
                        continue;
                    }

                    IntPtr stream;
                    int hrOpen = open(sensor, type, res, 0, 2, IntPtr.Zero, out stream);

                    if (hrOpen >= 0)
                        Console.WriteLine(string.Format("flags=0x{0:X8} tipo={1} res={2} -> OK (stream=0x{3:X})", flags, type, res, stream.ToInt64()));

                    shutdown(sensor);
                    release(sensor);
                }
            }
        }

        Console.WriteLine("fin");
    }
}
