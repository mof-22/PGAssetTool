using PGAssetTool.Core.Import.Audio;

namespace PGAssetTool.Core.Preview;

/// A decoded clip, ready to be looked at and listened to.
///
/// Hearing it is the only way to judge a replacement — a shot that is half a second too long or
/// twice as loud as everything around it is obvious to an ear and invisible in a file listing — and
/// seeing its shape answers the rest at a glance: whether a trim landed where it was meant to,
/// whether the level is anywhere near the original's.
public sealed record PreviewSound(PcmSound Sound)
{
    public int Channels => Sound.Channels;
    public int Frequency => Sound.Frequency;
    public float Seconds => Sound.Seconds;
    public int Frames => Sound.Frames;

    public string Describe =>
        $"{Seconds:0.00}s   {Frequency:N0} Hz   {(Channels == 1 ? "mono" : $"{Channels} channels")}";

    /// The lowest and highest sample in each column of a drawing that wide, in -1..1.
    ///
    /// A weapon sound is tens of thousands of samples and the pane is a few hundred pixels, so
    /// every column stands for thousands of them. Drawing the extremes of each is what makes a
    /// waveform look like the sound rather than like a solid block: taking one sample per column
    /// instead would alias a shot into whatever the sampling happened to land on.
    public (float Low, float High)[] Envelope(int columns)
    {
        var envelope = new (float Low, float High)[Math.Max(columns, 1)];
        if (Frames == 0)
        {
            Array.Fill(envelope, (0f, 0f));
            return envelope;
        }

        for (var column = 0; column < envelope.Length; column++)
        {
            var from = (int)((long)column * Frames / envelope.Length);
            var to = (int)((long)(column + 1) * Frames / envelope.Length);
            if (to <= from) to = from + 1;

            short low = short.MaxValue, high = short.MinValue;
            for (var frame = from; frame < to && frame < Frames; frame++)
            {
                // Channels are taken together: the two sides of a stereo clip are the same event,
                // and drawing them apart would say something about the mix rather than the sound.
                for (var channel = 0; channel < Channels; channel++)
                {
                    var sample = Sound.Samples[frame * Channels + channel];
                    if (sample < low) low = sample;
                    if (sample > high) high = sample;
                }
            }

            envelope[column] = low > high
                ? (0f, 0f)
                : (low / -(float)short.MinValue, high / (float)short.MaxValue);
        }
        return envelope;
    }

    /// The clip as a RIFF file, which is what a player wants handed to it.
    public byte[] ToWave() => WaveFile.Write(Sound);
}
