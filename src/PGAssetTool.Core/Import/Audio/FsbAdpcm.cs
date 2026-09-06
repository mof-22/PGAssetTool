using System.Buffers.Binary;

namespace PGAssetTool.Core.Import.Audio;

/// Decodes the IMA ADPCM the game's louder sounds are stored as.
///
/// Fmod5Sharp decodes this format too, but without saturating the predictor: where the signal
/// should flatten off at full scale it wraps to the opposite extreme instead, and the result is a
/// loud click. The synthwaver's shot has a hundred and fifty of them in two and a half seconds,
/// which is audible as a crackle over the whole sound. Everything else it decodes — plain PCM, and
/// Vorbis — is left to it.
///
/// The layout is not documented anywhere reachable and was read off the data: a block is 36 bytes
/// and carries 64 samples. The first four are the state the block starts from — a sixteen-bit
/// predictor, which is also the block's first sample, then the step index. The remaining thirty-two
/// hold nibbles, low half of each byte first, and the last one of the sixty-four is not used.
public static class FsbAdpcm
{
    public const int BlockBytes = 36;
    public const int BlockSamples = 64;

    private static readonly int[] Steps =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66,
        73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408,
        449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
        2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630,
        9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
        32767,
    ];

    private static readonly int[] Adjust = [-1, -1, -1, -1, 2, 4, 6, 8];

    /// <param name="encoded">The sample's own bytes, blocks of every channel in turn.</param>
    public static short[] Decode(ReadOnlySpan<byte> encoded, int channels, int frames)
    {
        if (channels < 1) channels = 1;

        var samples = new short[(long)frames * channels <= int.MaxValue ? frames * channels : 0];
        var stride = BlockBytes * channels;

        for (int at = 0, frame = 0; at + stride <= encoded.Length && frame < frames; at += stride)
        {
            for (var channel = 0; channel < channels; channel++)
                DecodeBlock(encoded.Slice(at + channel * BlockBytes, BlockBytes),
                    samples, frame, frames, channels, channel);

            frame += BlockSamples;
        }
        return samples;
    }

    private static void DecodeBlock(
        ReadOnlySpan<byte> block, short[] into, int frame, int frames, int channels, int channel)
    {
        int predictor = BinaryPrimitives.ReadInt16LittleEndian(block);
        var index = Math.Clamp(block[2], 0, Steps.Length - 1);

        Put(into, frame, frames, channels, channel, predictor);

        // Sixty-three nibbles, not sixty-four: the block carries one more than it uses.
        for (var i = 0; i < BlockSamples - 1; i++)
        {
            var pair = block[4 + i / 2];
            var nibble = i % 2 == 0 ? pair & 0xF : pair >> 4;

            var step = Steps[index];
            var difference = step >> 3;
            if ((nibble & 1) != 0) difference += step >> 2;
            if ((nibble & 2) != 0) difference += step >> 1;
            if ((nibble & 4) != 0) difference += step;
            if ((nibble & 8) != 0) difference = -difference;

            // Saturating, not wrapping. This one line is the whole difference between a shot that
            // sounds like a shot and one that crackles.
            predictor = Math.Clamp(predictor + difference, short.MinValue, short.MaxValue);
            index = Math.Clamp(index + Adjust[nibble & 7], 0, Steps.Length - 1);

            Put(into, frame + i + 1, frames, channels, channel, predictor);
        }
    }

    private static void Put(short[] into, int frame, int frames, int channels, int channel, int value)
    {
        if (frame >= frames) return;
        var at = frame * channels + channel;
        if (at < into.Length) into[at] = (short)value;
    }
}
