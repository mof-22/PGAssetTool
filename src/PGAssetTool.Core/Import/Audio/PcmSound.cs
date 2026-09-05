namespace PGAssetTool.Core.Import.Audio;

/// Decoded audio, whatever it arrived as.
///
/// Sixteen bits is where everything is heading: the bank written from this is PCM16 and the game's
/// own clips are all 16-bit, so widening anywhere in between would only be undone.
public sealed record PcmSound(short[] Samples, int Channels, int Frequency)
{
    public int Frames => Samples.Length / Channels;
    public float Seconds => (float)Frames / Frequency;

    /// MP3 and Vorbis both decode to interleaved floats nominally in -1..1, but neither format
    /// guarantees it — a loud master can decode past full scale, and wrapping is far worse to listen
    /// to than clipping.
    public static PcmSound FromFloat(IReadOnlyList<float> samples, int channels, int frequency)
    {
        var pcm = new short[samples.Count];
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (short)Math.Clamp(Math.Round(samples[i] * short.MaxValue), short.MinValue, short.MaxValue);
        return new PcmSound(pcm, channels, frequency);
    }
}
