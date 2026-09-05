using AssetsTools.NET;

namespace PGAssetTool.Core.Import.Audio;

public sealed record AudioChange(
    string Format, int Channels, int Frequency, float OldSeconds, float NewSeconds, long Bytes)
{
    public override string ToString()
        => $"{Frequency}Hz {Channels}ch, {OldSeconds:F2}s->{NewSeconds:F2}s, {Bytes:N0} bytes as PCM16"
           + (Format == "pcm16" ? "" : $" (was {Format})");
}

/// Replaces an AudioClip's sound with a WAV.
///
/// The clip is rewritten as PCM even though the game ships Vorbis and ADPCM. Its bundled FMOD
/// runtime is old enough to reject a modern fsbankcl `fadpcm` build outright — `Unsupported file or
/// audio format` — while PCM plays, and matching the original codec would mean shipping an encoder
/// to save space the player never notices.
public static class AudioImporter
{
    private const int PcmCompressionFormat = 0;   // Unity's AudioCompressionFormat.PCM

    /// <param name="append">
    /// Puts the new bank into the bundle's shared resource stream and answers with its offset. The
    /// payload lives outside the object, so there is nothing to write in place.
    /// </param>
    public static AudioChange Replace(AssetTypeValueField field, string sourcePath, Func<string, byte[], long> append)
    {
        var resource = field["m_Resource"];
        if (resource.IsDummy)
            throw new InvalidDataException($"'{field["m_Name"].AsString}' has no m_Resource to repoint.");

        var wave = SoundFile.Read(sourcePath);
        var was = Describe(field);
        var name = field["m_Name"].AsString;

        var bank = Fsb5Writer.Write(wave.Samples, wave.Channels, wave.Frequency, name);
        var source = resource["m_Source"].AsString;
        var offset = append(source, bank);

        resource["m_Offset"].AsLong = offset;
        resource["m_Size"].AsLong = bank.Length;

        field["m_CompressionFormat"].AsInt = PcmCompressionFormat;
        field["m_Channels"].AsInt = wave.Channels;
        field["m_Frequency"].AsInt = wave.Frequency;
        field["m_BitsPerSample"].AsInt = 16;
        field["m_Length"].AsFloat = wave.Seconds;
        if (!field["m_SubsoundIndex"].IsDummy) field["m_SubsoundIndex"].AsInt = 0;

        return new AudioChange(was.Format, wave.Channels, wave.Frequency, was.Seconds, wave.Seconds, bank.Length);
    }

    private static (string Format, float Seconds) Describe(AssetTypeValueField field)
        => (field["m_CompressionFormat"].AsInt switch
        {
            0 => "pcm16",
            1 => "vorbis",
            2 => "adpcm",
            3 => "mp3",
            var other => $"format {other}",
        }, field["m_Length"].AsFloat);
}
