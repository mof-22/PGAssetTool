using NLayer;

namespace PGAssetTool.Core.Import.Audio;

/// Decodes an MP3, and throws away the silence the format forces onto both ends.
///
/// MP3 cannot represent an arbitrary length: the encoder pads the audio out to whole 576- or
/// 1152-sample frames and prepends its own lookahead, so decoding gives back more than went in.
/// LAME records how much in a tag, which is the only way to get the original length back. Left
/// alone it puts about fifty milliseconds of silence in front of every sound — inaudible on music,
/// obvious on a gunshot.
public static class Mp3File
{
    /// Every MP3 decoder owes the stream this much on top of whatever the encoder declared. It is a
    /// property of the format's filterbank, not of the file.
    private const int DecoderDelay = 529;

    public static PcmSound Parse(byte[] raw, string what)
    {
        using var stream = new MemoryStream(raw);
        using var mp3 = new MpegFile(stream);

        var samples = new List<float>((int)Math.Min(mp3.Length / sizeof(float), 1 << 24));
        var block = new float[mp3.Channels * 4096];

        for (int read; (read = mp3.ReadSamples(block, 0, block.Length)) > 0;)
            samples.AddRange(block.AsSpan(0, read));

        if (samples.Count == 0) throw new InvalidDataException($"'{what}' decoded to no audio.");

        var (lead, tail) = Gapless(raw);
        var frames = samples.Count / mp3.Channels;
        var keep = Math.Clamp(frames - lead - tail, 0, frames);
        var from = Math.Min(lead, frames) * mp3.Channels;

        return PcmSound.FromFloat(
            samples.GetRange(from, keep * mp3.Channels), mp3.Channels, mp3.SampleRate);
    }

    /// Reads LAME's delay and padding out of the Xing header in the first frame.
    ///
    /// Without one — a stream cut out of something larger, an encoder that writes no tag — there is
    /// nothing to trim by, and guessing would cut real audio off the front.
    private static (int Lead, int Tail) Gapless(byte[] raw)
    {
        var at = SkipId3(raw);

        // The tag lives in the first frame's data, past a side-info block whose size depends on the
        // version and channel mode. Rather than decode that, look for the magic within the frame.
        var limit = Math.Min(raw.Length - 4, at + 1024);
        for (; at < limit; at++)
        {
            var magic = System.Text.Encoding.ASCII.GetString(raw, at, 4);
            if (magic is not ("Xing" or "Info")) continue;

            var flags = ReadBigEndian(raw, at + 4);
            var lame = at + 8
                + ((flags & 1) != 0 ? 4 : 0)      // frame count
                + ((flags & 2) != 0 ? 4 : 0)      // byte count
                + ((flags & 4) != 0 ? 100 : 0)    // seek table
                + ((flags & 8) != 0 ? 4 : 0);     // quality

            // Nine bytes of encoder name, then fields nobody here cares about, then three bytes
            // holding two twelve-bit counts.
            var counts = lame + 9 + 12;
            if (counts + 3 > raw.Length) return (0, 0);

            var delay = raw[counts] << 4 | raw[counts + 1] >> 4;
            var padding = (raw[counts + 1] & 0x0F) << 8 | raw[counts + 2];

            return (delay + DecoderDelay, Math.Max(0, padding - DecoderDelay));
        }

        return (0, 0);
    }

    private static int SkipId3(byte[] raw)
    {
        if (raw.Length < 10 || raw[0] != 'I' || raw[1] != 'D' || raw[2] != '3') return 0;

        // The size is seven bits per byte so it can never look like a frame sync.
        var size = raw[6] << 21 | raw[7] << 14 | raw[8] << 7 | raw[9];
        return Math.Min(10 + size, raw.Length);
    }

    private static uint ReadBigEndian(byte[] raw, int at)
        => (uint)(raw[at] << 24 | raw[at + 1] << 16 | raw[at + 2] << 8 | raw[at + 3]);
}
