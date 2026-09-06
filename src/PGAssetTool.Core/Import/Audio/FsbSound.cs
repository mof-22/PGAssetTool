using Fmod5Sharp;
using Fmod5Sharp.FmodTypes;

namespace PGAssetTool.Core.Import.Audio;

/// Reads the first clip out of an FMOD bank, which is where an AudioClip's bytes actually live.
///
/// One place, because the exporter and the preview have to agree: a sound that plays one way in the
/// window and writes another way to disk is worse than either being wrong on its own.
public static class FsbSound
{
    public static PcmSound? Read(byte[] bank)
    {
        if (!FsbLoader.TryLoadFsbFromByteArray(bank, out var loaded) || loaded is null) return null;
        return loaded.Samples.FirstOrDefault() is { } sample ? Read(sample, loaded.Header) : null;
    }

    public static PcmSound? Read(FmodSample sample, FmodAudioHeader header)
    {
        var channels = Math.Max((int)sample.Metadata.Channels, 1);
        var frequency = (int)sample.Metadata.Frequency;

        // Decoded here rather than by the library, which does not saturate the predictor: see
        // FsbAdpcm. Everything else the library handles correctly and is left to it.
        if (header.AudioType == FmodAudioType.IMAADPCM)
            return new PcmSound(
                FsbAdpcm.Decode(sample.SampleBytes, channels, (int)sample.Metadata.SampleCount),
                channels, frequency);

        if (!sample.RebuildAsStandardFileFormat(out var data, out var extension) || data is null)
            return null;

        // The rebuilt WAV leaves both of its length fields at zero; see WaveFile.
        return SoundFile.Parse(
            extension == "wav" ? WaveFile.WithLengthsFilledIn(data) : data, $"a {extension} clip");
    }
}
