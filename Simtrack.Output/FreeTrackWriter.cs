using System.Buffers.Binary;
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

    /// <summary>
    /// Búfer con el que se escribe el bloque. Se reutiliza para no asignar
    /// memoria ni pasar por el marshaling de estructuras en cada escritura
    /// (a 250 Hz se nota).
    /// </summary>
    private readonly byte[] _buffer = new byte[FreeTrackLayout.HeapSize];

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

        byte[] buffer = _buffer;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 1);          // DataID
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), CamWidth);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), CamHeight);

        WriteSingle(buffer, 12, pose.Yaw);      // Yaw
        WriteSingle(buffer, 16, pose.Pitch);    // Pitch
        WriteSingle(buffer, 20, pose.Roll);     // Roll
        WriteSingle(buffer, 24, pose.X);
        WriteSingle(buffer, 28, pose.Y);
        WriteSingle(buffer, 32, pose.Z);

        // Pose "raw" (sin suavizar): mismos valores, la mayoría de juegos la ignora.
        WriteSingle(buffer, 36, pose.Yaw);
        WriteSingle(buffer, 40, pose.Pitch);
        WriteSingle(buffer, 44, pose.Roll);
        WriteSingle(buffer, 48, pose.X);
        WriteSingle(buffer, 52, pose.Y);
        WriteSingle(buffer, 56, pose.Z);

        // A partir de aquí: puntos IR (60..92), GameID, tabla y GameID2 (92..108).
        // Se quedan a cero; no hace falta reescribirlos en cada iteración.

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
            view.WriteArray(0, buffer, 0, buffer.Length);
            Interlocked.Increment(ref _writeCount);

            _lastWriteAt = DateTime.UtcNow;
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

    private static void WriteSingle(byte[] buffer, int offset, double value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), (float)value);
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
