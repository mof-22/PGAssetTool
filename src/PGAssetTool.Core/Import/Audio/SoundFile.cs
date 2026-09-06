namespace PGAssetTool.Core.Import.Audio;

/// Reads whichever of the three formats a file turns out to be.
///
/// The two the exporter writes are covered — WAV for the PCM and ADPCM clips, Ogg Vorbis for the
/// rest — so anything taken out of the game can go back in after editing. MP3 is here because it is
/// what people already have.
///
/// Dispatch is on content, not extension: a file renamed to .wav is still whatever it was, and the
/// error for an unreadable one should say what it actually is.
public static class SoundFile
{
    public static IReadOnlyList<string> Extensions { get; } = ["wav", "mp3", "ogg"];

    public static PcmSound Read(string path) => Parse(File.ReadAllBytes(path), Path.GetFileName(path));

    public static PcmSound Parse(byte[] raw, string what)
    {
        return Magic(raw) switch
        {
            "RIFF" => WaveFile.Parse(raw, what),
            "OggS" => OggFile.Parse(raw, what),
            "mp3" => Mp3File.Parse(raw, what),
            var other => throw new NotSupportedException(
                $"'{what}' is {other}. Give it a WAV, MP3 or Ogg Vorbis file."),
        };
    }

    private static string Magic(byte[] raw)
    {
        if (raw.Length < 4) return "empty";
        var head = System.Text.Encoding.ASCII.GetString(raw, 0, 4);
        if (head is "RIFF" or "OggS") return head;

        // MP3 has no header of its own: either an ID3 tag, or straight into a frame whose sync word
        // is eleven set bits.
        if (head.StartsWith("ID3") || (raw[0] == 0xFF && (raw[1] & 0xE0) == 0xE0)) return "mp3";

        return head.All(c => c is >= ' ' and < (char)127) ? $"a '{head}' file" : "not a sound file this reads";
    }
}
