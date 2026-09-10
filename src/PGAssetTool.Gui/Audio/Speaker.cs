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

    /// When the clip that is playing runs out.
    ///
    /// PlaySound does not say whether it is still going, and asking Windows would mean the much
    /// larger waveOut interface. The length of the clip is already known, so the end is arithmetic:
    /// good enough for a key that means "play, or stop if it is still playing", and wrong only in
    /// the moment either answer would do.
    private static DateTime _until = DateTime.MinValue;
    private static TimeSpan _length;

    public static bool IsPlaying => DateTime.UtcNow < _until;

    /// Which clip is playing, as whatever the caller handed over to identify it. The editor puts
    /// two waveforms on the page and only one of them is making a sound.
    public static object? Sounding { get; private set; }

    /// How far through the clip is, from 0 at the start to 1 at the end. Zero when nothing plays.
    ///
    /// Arithmetic from the length rather than a question put to Windows, for the same reason the
    /// end is: PlaySound says nothing about where it has got to, and asking would mean the much
    /// larger waveOut interface for a line that has to be somewhere near right, not exact.
    public static double Through => IsPlaying && _length > TimeSpan.Zero
        ? 1 - (_until - DateTime.UtcNow) / _length
        : 0;

    /// Raised when a clip starts or stops, so a drawing of one can follow it.
    public static event Action? Changed;

    public static void Play(byte[] wave, TimeSpan length, object? source = null)
    {
        Stop();
        _pinned = GCHandle.Alloc(wave, GCHandleType.Pinned);
        if (PlaySound(_pinned.AddrOfPinnedObject(), IntPtr.Zero, Memory | Async | NoDefault))
        {
            (_until, _length, Sounding) = (DateTime.UtcNow + length, length, source);
            Changed?.Invoke();
        }
        else
        {
            Stop();
        }
    }

    public static void Stop()
    {
        PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        if (_pinned.IsAllocated) _pinned.Free();
        (_until, _length, Sounding) = (DateTime.MinValue, TimeSpan.Zero, null);
        Changed?.Invoke();
    }
}
