using System;
using System.Runtime.InteropServices;

namespace Simtrack.Kinect
{
    /// <summary>
    /// Captura del flujo infrarrojo del Kinect v1 con la API nativa de
    /// Kinect10.dll (INuiSensor), sin pasar por el SDK gestionado.
    /// Es lo mismo que hace KinectCam por dentro.
    /// </summary>
    internal sealed class NativeIr : IDisposable
    {
        public const int ImageTypeInfrared = 5;
        public const int Resolution640x480 = 2;

        [DllImport("Kinect10.dll")]
        private static extern int NuiGetSensorCount(out int pCount);

        [DllImport("Kinect10.dll")]
        private static extern int NuiCreateSensorByIndex(int index, out IntPtr ppSensor);

        private IntPtr _sensor;
        private IntPtr _stream;
        private IntPtr _vtable;
        private IntPtr _textureVtable;

        private InitializeDelegate _initialize;
        private ShutdownDelegate _shutdown;
        private ImageStreamOpenDelegate _streamOpen;
        private ImageStreamGetNextFrameDelegate _getNextFrame;
        private ImageStreamReleaseFrameDelegate _releaseFrame;
        private ReleaseDelegate _release;
        private LockRectDelegate _lockRect;
        private UnlockRectDelegate _unlockRect;

        private readonly byte[] _frameBuffer = new byte[128];
        private readonly byte[] _lockedRect = new byte[16];

        public int Width { get; private set; }
        public int Height { get; private set; }

        public bool IsOpen
        {
            get { return _sensor != IntPtr.Zero && _stream != IntPtr.Zero; }
        }

        public void Open()
        {
            int count;
            Console.WriteLine("IR: NuiGetSensorCount...");
            int hr = NuiGetSensorCount(out count);
            Console.WriteLine("IR: sensores=" + count + " hr=0x" + hr.ToString("X8"));

            if (hr < 0 || count <= 0)
                throw new InvalidOperationException("No hay ningun sensor Kinect (HRESULT " + hr + ").");

            Console.WriteLine("IR: NuiCreateSensorByIndex...");
            hr = NuiCreateSensorByIndex(0, out _sensor);
            Console.WriteLine("IR: sensor=0x" + _sensor.ToString("X") + " hr=0x" + hr.ToString("X8"));

            if (hr < 0 || _sensor == IntPtr.Zero)
                throw new InvalidOperationException("NuiCreateSensorByIndex fallo (HRESULT " + hr + ").");

            Console.WriteLine("IR: leyendo vtable...");
            _vtable = Marshal.ReadIntPtr(_sensor);
            Console.WriteLine("IR: vtable=0x" + _vtable.ToString("X"));

            _initialize = GetDelegate<InitializeDelegate>(3);
            _shutdown = GetDelegate<ShutdownDelegate>(4);
            _streamOpen = GetDelegate<ImageStreamOpenDelegate>(6);
            _getNextFrame = GetDelegate<ImageStreamGetNextFrameDelegate>(9);
            _releaseFrame = GetDelegate<ImageStreamReleaseFrameDelegate>(10);
            _release = GetDelegate<ReleaseDelegate>(2);

            Console.WriteLine("IR: NuiInitialize...");
            // 0x2 habilita el flujo de color/IR (comprobado con el sensor real).
            hr = _initialize(_sensor, 0x2);
            Console.WriteLine("IR: init hr=0x" + hr.ToString("X8"));

            if (hr < 0)
            {
                hr = _initialize(_sensor, 0x1);
                Console.WriteLine("IR: init alternativo hr=0x" + hr.ToString("X8"));
            }

            if (hr < 0)
            {
                _shutdown(_sensor);
                hr = _initialize(_sensor, 0x04);
            }

            if (hr < 0)
                throw new InvalidOperationException("NuiInitialize fallo (HRESULT 0x" + hr.ToString("X8") + ").");

            Console.WriteLine("IR: NuiImageStreamOpen...");
            hr = _streamOpen(_sensor, ImageTypeInfrared, Resolution640x480, 0, 2, IntPtr.Zero, out _stream);
            Console.WriteLine("IR: stream=0x" + _stream.ToString("X") + " hr=0x" + hr.ToString("X8"));

            if (hr < 0 || _stream == IntPtr.Zero)
                throw new InvalidOperationException("NuiImageStreamOpen (IR) fallo (HRESULT 0x" + hr.ToString("X8") + ").");

            Width = 640;
            Height = 480;
        }

        private T GetDelegate<T>(int slot) where T : class
        {
            IntPtr fn = Marshal.ReadIntPtr(_vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
        }

        /// <summary>Lee un fotograma IR. Devuelve null si no hay ninguno.</summary>
        private int _debugCalls;

        public byte[] GetFrame(int timeoutMs)
        {
            if (!IsOpen)
                return null;

            int hr = _getNextFrame(_sensor, _stream, (uint)timeoutMs, _frameBuffer);

            if (_debugCalls < 5)
            {
                Console.WriteLine("IR frame hr=0x" + hr.ToString("X8") + " bytes=" + _frameBuffer[0].ToString());
                _debugCalls++;
            }

            if (hr < 0)
                return null;

            IntPtr texture = Marshal.ReadIntPtr(_frameBuffer, 24);

            if (texture == IntPtr.Zero)
            {
                _releaseFrame(_sensor, _stream, _frameBuffer);
                return null;
            }

            _textureVtable = Marshal.ReadIntPtr(texture);

            if (_lockRect == null)
            {
                IntPtr lockPtr = Marshal.ReadIntPtr(_textureVtable, 3 * IntPtr.Size);
                IntPtr unlockPtr = Marshal.ReadIntPtr(_textureVtable, 4 * IntPtr.Size);

                _lockRect = (LockRectDelegate)Marshal.GetDelegateForFunctionPointer(lockPtr, typeof(LockRectDelegate));
                _unlockRect = (UnlockRectDelegate)Marshal.GetDelegateForFunctionPointer(unlockPtr, typeof(UnlockRectDelegate));
            }

            if (_debugCalls < 6)
                Console.WriteLine("IR texture=0x" + texture.ToString("X"));

            hr = _lockRect(texture, 0, _lockedRect, IntPtr.Zero, 0);

            if (_debugCalls < 6)
                Console.WriteLine("IR lock hr=0x" + hr.ToString("X8") + " pitch=" + BitConverter.ToInt32(_lockedRect, 0) + " size=" + BitConverter.ToInt32(_lockedRect, 4));

            if (hr < 0)
            {
                _releaseFrame(_sensor, _stream, _frameBuffer);
                return null;
            }

            int pitch = BitConverter.ToInt32(_lockedRect, 0);
            int size = BitConverter.ToInt32(_lockedRect, 4);
            IntPtr bits = Marshal.ReadIntPtr(_lockedRect, 8);

            byte[] result = null;

            try
            {
                if (size > 0 && bits != IntPtr.Zero)
                {
                    var raw = new byte[Width * Height * 2];
                    int rowBytes = Width * 2;
                    int copy = Math.Min(pitch, rowBytes > 0 ? pitch : rowBytes);

                    for (var y = 0; y < Height; y++)
                    {
                        IntPtr source = IntPtr.Add(bits, y * pitch);
                        Marshal.Copy(source, raw, y * rowBytes, Math.Min(rowBytes, size - y * pitch));
                    }

                    result = raw;
                }
            }
            catch
            {
                result = null;
            }
            finally
            {
                _unlockRect(texture, 0);
                _releaseFrame(_sensor, _stream, _frameBuffer);
            }

            return result;
        }

        public void Close()
        {
            if (_sensor != IntPtr.Zero)
            {
                try
                {
                    if (_shutdown != null)
                        _shutdown(_sensor);

                    if (_release != null)
                        _release(_sensor);
                }
                catch
                {
                }

                _sensor = IntPtr.Zero;
                _stream = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Close();
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitializeDelegate(IntPtr self, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ShutdownDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ImageStreamOpenDelegate(IntPtr self, int imageType, int resolution, uint flags, uint limit, IntPtr nextFrameEvent, out IntPtr stream);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ImageStreamGetNextFrameDelegate(IntPtr self, IntPtr stream, uint waitMs, [In, Out] byte[] frame);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ImageStreamReleaseFrameDelegate(IntPtr self, IntPtr stream, [In, Out] byte[] frame);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int LockRectDelegate(IntPtr self, uint level, [In, Out] byte[] lockedRect, IntPtr rect, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int UnlockRectDelegate(IntPtr self, uint level);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseDelegate(IntPtr self);
    }
}
