using PGAssetTool.Core.Import.Audio;

namespace PGAssetTool.Core.Tests;

/// The fixtures are the same 0.2 second 440 Hz tone encoded three ways, so the readers can be held
/// against each other rather than against numbers copied out of a run.
public class SoundFileTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static readonly PcmSound Wav = SoundFile.Read(Fixture("tone.wav"));

    [Theory]
    [InlineData("tone.wav")]
    [InlineData("tone.mp3")]
    [InlineData("tone.ogg")]
    public void EveryFormatDecodesToTheSameShapeOfAudio(string file)
    {
        var sound = SoundFile.Read(Fixture(file));

        Assert.Equal(1, sound.Channels);
        Assert.Equal(22050, sound.Frequency);
        Assert.Equal(Wav.Frames, sound.Frames);
        Assert.Equal(0.2f, sound.Seconds, 3);
    }

    [Theory]
    [InlineData("tone.mp3")]
    [InlineData("tone.ogg")]
    public void ALossyDecodeIsTheSameSoundNotJustTheSameLength(string file)
    {
        // Neither format comes back sample for sample, so this asks whether it is recognisably the
        // tone: the error has to sit well below the signal.
        var sound = SoundFile.Read(Fixture(file));

        double error = 0, signal = 0;
        for (int i = 0; i < Wav.Samples.Length; i++)
        {
            double reference = Wav.Samples[i];
            error += Math.Pow(reference - sound.Samples[i], 2);
            signal += reference * reference;
        }

        Assert.True(Math.Sqrt(error / signal) < 0.15,
            $"{file} decoded to something {Math.Sqrt(error / signal):P0} away from the tone");
    }

    [Fact]
    public void TheFormatIsDecidedByContentNotByTheNameOnTheFile()
    {
        // People rename things. An MP3 called .wav is still an MP3, and saying "not a WAV" would be
        // an unhelpful thing to tell them.
        var renamed = Path.Combine(Path.GetTempPath(), $"lying-{Guid.NewGuid():n}.wav");
        File.Copy(Fixture("tone.mp3"), renamed);
        try
        {
            Assert.Equal(Wav.Frames, SoundFile.Read(renamed).Frames);
        }
        finally
        {
            File.Delete(renamed);
        }
    }

    [Fact]
    public void SomethingThatIsNotAudioSaysSoAndSaysWhatIsAccepted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"notaudio-{Guid.NewGuid():n}.wav");
        File.WriteAllText(path, "this is not a sound at all");
        try
        {
            var error = Assert.Throws<NotSupportedException>(() => SoundFile.Read(path));
            Assert.Contains("WAV, MP3 or Ogg Vorbis", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnMp3StartsWhereItsSourceDidRatherThanFiftyMillisecondsLate()
    {
        // MP3 pads to whole frames and prepends the encoder's lookahead, so a naive decode of this
        // fixture returns 5760 frames with 1105 of silence in front. Inaudible on music, obvious on
        // a gunshot. LAME's tag says how much to cut.
        var mp3 = SoundFile.Read(Fixture("tone.mp3"));

        Assert.Equal(Wav.Frames, mp3.Frames);
        Assert.Equal(Loudness(Wav.Samples[..200]), Loudness(mp3.Samples[..200]), 0.15 * short.MaxValue);
    }

    [Fact]
    public void AnMp3WithNoTagToTrimByIsLeftAlone()
    {
        // Cutting a fixed amount off anything untagged would eat real audio. A stream with no Xing
        // header decodes whole, which is also what ffmpeg does with it.
        var untagged = Path.Combine(Path.GetTempPath(), $"untagged-{Guid.NewGuid():n}.mp3");
        var raw = File.ReadAllBytes(Fixture("tone.mp3"));

        // Blank the tag where it sits, leaving the frame around it intact.
        var text = System.Text.Encoding.ASCII.GetString(raw);
        var at = Math.Max(text.IndexOf("Xing", StringComparison.Ordinal), text.IndexOf("Info", StringComparison.Ordinal));
        Assert.True(at > 0, "the fixture was supposed to carry a Xing or Info header");
        "Junk"u8.CopyTo(raw.AsSpan(at));
        File.WriteAllBytes(untagged, raw);

        try
        {
            Assert.True(SoundFile.Read(untagged).Frames > Wav.Frames);
        }
        finally
        {
            File.Delete(untagged);
        }
    }

    private static double Loudness(IEnumerable<short> samples)
        => Math.Sqrt(samples.Average(s => (double)s * s));
}
