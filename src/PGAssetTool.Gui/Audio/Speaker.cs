using System.Runtime.InteropServices;

namespace PGAssetTool.Gui.Audio;

/// Plays one clip at a time, through Windows' own sound API.
///
/// Avalonia has no audio, and the libraries that do would each add a dependency and a device
/// lifecycle to get wrong. PlaySound takes a WAV in memory, plays it on its own thread and stops
/// when told — which is the whole of what this needs. Only one sound plays at once, which is not a
/// limitation here: starting a second clip while the first runs would tell you nothing about either.
public static class Speaker
{
    private const int Memory = 0x00000004;      // the pointer is a WAV in memory, not a filename
    private const int Async = 0x00000001;       // return immediately rather than blocking the UI
    private const int NoDefault = 0x00000002;   // silence rather than the system beep on failure

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, int flags);

    /// The bytes have to outlive the call: PlaySound reads from them for as long as it plays, and a
    /// managed array can be moved by a collection at any moment. Pinned here and freed when the next
    /// clip starts or playing stops, which is also what keeps two clips from overlapping.
    private static GCHandle _pinned;

    public static void Play(byte[] wave)
    {
        Stop();
        _pinned = GCHandle.Alloc(wave, GCHandleType.Pinned);
        if (!PlaySound(_pinned.AddrOfPinnedObject(), IntPtr.Zero, Memory | Async | NoDefault)) Stop();
    }

    public static void Stop()
    {
        PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        if (_pinned.IsAllocated) _pinned.Free();
    }
}
