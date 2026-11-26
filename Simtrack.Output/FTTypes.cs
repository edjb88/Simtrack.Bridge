using System.Runtime.InteropServices;

namespace Simtrack.Output;

/// <summary>
/// Estructura exacta que espera FreeTrack 2.0 (y por tanto TrackIR) en la
/// memoria compartida "FT_SharedMem".
/// Copiada de fttypes.h de OpenTrack (protocolo FreeTrack original).
///
/// OJO: no basta con los 9 primeros campos (eso serían 36 bytes). El bloque
/// real mide 92 bytes e incluye la pose "raw" y los 4 puntos IR. Si el
/// tamaño no es 92, los juegos leen datos desplazados.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FTData
{
    public uint DataID;      // debe valer 1
    public int CamWidth;     // 100 típico
    public int CamHeight;    // 250 típico

    // Pose virtual (la que usa el juego)
    public float Yaw;        // radianes, positivo hacia la izquierda
    public float Pitch;      // radianes, positivo hacia arriba
    public float Roll;       // radianes, positivo hacia la izquierda
    public float X;          // centímetros
    public float Y;          // centímetros
    public float Z;          // centímetros

    // Pose "raw" sin suavizar (la mayoría de juegos la ignora)
    public float RawYaw;
    public float RawPitch;
    public float RawRoll;
    public float RawX;
    public float RawY;
    public float RawZ;

    // Puntos IR (X1..Y4). Sin sistema IR se quedan a cero.
    public float X1;
    public float Y1;
    public float X2;
    public float Y2;
    public float X3;
    public float Y3;
    public float X4;
    public float Y4;
}

/// <summary>
/// Bloque completo de la memoria compartida: 92 bytes de FTData + GameID +
/// tabla + GameID2 = 108 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FTHeap
{
    public FTData Data;      // 92 bytes
    public int GameID;
    public int TableInt0;
    public int TableInt1;
    public int GameID2;
}

public static class FreeTrackLayout
{
    public const string SharedMemoryName = "FT_SharedMem";
    public const string MutexName = "FT_Mutext";
    public const int HeapSize = 108;

    public static int DataSize => Marshal.SizeOf<FTData>();
    public static int HeapSizeNormalized => Marshal.SizeOf<FTHeap>();
}
