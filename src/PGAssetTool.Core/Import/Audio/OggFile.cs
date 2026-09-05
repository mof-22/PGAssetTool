using NVorbis;

namespace PGAssetTool.Core.Import.Audio;

public static class OggFile
{
    public static PcmSound Parse(byte[] raw, string what)
    {
        using var stream = new MemoryStream(raw);
        using var vorbis = new VorbisReader(stream);

        var total = checked((int)(vorbis.TotalSamples * vorbis.Channels));
        var samples = new float[total];

        var at = 0;
        for (int read; at < total && (read = vorbis.ReadSamples(samples, at, total - at)) > 0;)
            at += read;

        if (at == 0) throw new InvalidDataException($"'{what}' decoded to no audio.");
        return PcmSound.FromFloat(samples.AsSpan(0, at).ToArray(), vorbis.Channels, vorbis.SampleRate);
    }
}
