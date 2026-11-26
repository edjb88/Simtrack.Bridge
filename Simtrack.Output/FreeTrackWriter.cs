using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Simtrack.Core.Tracking;

namespace Simtrack.Output;

/// <summary>
/// Escribe la pose en la memoria compartida de FreeTrack 2.0.
///
/// Cualquier simulador que soporte TrackIR de forma nativa (Assetto Corsa
/// EVO, AC, iRacing, etc.) busca el bloque "FT_SharedMem" y lee de ahí.
/// </summary>
public sealed class FreeTrackWriter : IDisposable
{
    private readonly object _gate = new();

    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private Mutex? _mutex;

    private long _writeCount;
    private DateTime _lastWriteAt = DateTime.MinValue;

    public bool IsOpen
    {
        get { lock (_gate) { return _view is not null; } }
    }

    public long WriteCount => Interlocked.Read(ref _writeCount);

    public DateTime LastWriteAt
    {
        get { lock (_gate) { return _lastWriteAt; } }
    }

    /// <summary>Ancho y alto de cámara que se anuncian al juego (valores típicos de TrackIR).</summary>
    public int CamWidth { get; set; } = 100;
    public int CamHeight { get; set; } = 250;

    public void Open()
    {
        lock (_gate)
        {
            if (_view is not null)
                return;

            if (FreeTrackLayout.HeapSizeNormalized != FreeTrackLayout.HeapSize)
            {
                throw new InvalidOperationException(
                    $"FTHeap mide {FreeTrackLayout.HeapSizeNormalized} bytes y FreeTrack espera " +
                    $"{FreeTrackLayout.HeapSize}. Revisa FTTypes.cs.");
            }

            _file = MemoryMappedFile.CreateOrOpen(FreeTrackLayout.SharedMemoryName, FreeTrackLayout.HeapSize);
            _view = _file.CreateViewAccessor(0, FreeTrackLayout.HeapSize);

            // FreeTrack usa también un mutex con la errata original "FT_Mutext".
            // Si existe, se reutiliza; si no, se crea.
            _mutex = new Mutex(initiallyOwned: false, FreeTrackLayout.MutexName);

            // Primera escritura: DataID = 1 para que el juego detecte la señal.
            WriteCore(PoseSample.Zero);
        }
    }

    public void Write(PoseSample pose)
    {
        lock (_gate)
        {
            if (_view is null)
                return;

            WriteCore(pose);
        }
    }

    private void WriteCore(PoseSample pose)
    {
        MemoryMappedViewAccessor view = _view!;

        var heap = new FTHeap
        {
            Data = new FTData
            {
                DataID = 1,
                CamWidth = CamWidth,
                CamHeight = CamHeight,
                Yaw = (float)pose.Yaw,
                Pitch = (float)pose.Pitch,
                Roll = (float)pose.Roll,
                X = (float)pose.X,
                Y = (float)pose.Y,
                Z = (float)pose.Z,
                RawYaw = (float)pose.Yaw,
                RawPitch = (float)pose.Pitch,
                RawRoll = (float)pose.Roll,
                RawX = (float)pose.X,
                RawY = (float)pose.Y,
                RawZ = (float)pose.Z
            },
            GameID = 0,
            GameID2 = 0
        };

        bool locked = false;

        try
        {
            locked = _mutex?.WaitOne(2) ?? false;
        }
        catch (AbandonedMutexException)
        {
            locked = true;
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            view.Write(0, ref heap);
            Interlocked.Increment(ref _writeCount);

            lock (_gate)
            {
                _lastWriteAt = DateTime.UtcNow;
            }
        }
        finally
        {
            if (locked)
            {
                try
                {
                    _mutex!.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }
        }
    }

    /// <summary>Lee el bloque actual (para mostrar la pose que ve el juego / depurar).</summary>
    public static FTHeap? ReadCurrent()
    {
        try
        {
            using MemoryMappedFile file = MemoryMappedFile.OpenExisting(FreeTrackLayout.SharedMemoryName);
            using MemoryMappedViewAccessor view = file.CreateViewAccessor(0, FreeTrackLayout.HeapSize);

            var heap = default(FTHeap);
            view.Read(0, out heap);

            return heap;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _view?.Dispose();
            _file?.Dispose();

            _view = null;
            _file = null;

            _mutex?.Dispose();
            _mutex = null;
        }
    }

    public void Dispose()
    {
        Close();
    }
}
