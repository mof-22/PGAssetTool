using System.Globalization;

namespace PGAssetTool.Cli;

/// Column alignment that counts what a terminal actually draws. CJK text renders two cells wide,
/// so padding by character count misaligns every column once a name is not Latin.
public static class TextColumn
{
    public static string Pad(string? text, int width)
    {
        text ??= "";
        var padding = width - DisplayWidth(text);
        return padding > 0 ? text + new string(' ', padding) : text;
    }

    public static int DisplayWidth(string text)
    {
        var width = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            width += IsWide(char.ConvertToUtf32((string)enumerator.Current, 0)) ? 2 : 1;
        return width;
    }

    private static bool IsWide(int codePoint) => codePoint
        is >= 0x1100 and <= 0x115F      // Hangul Jamo
        or >= 0x2E80 and <= 0x303E      // CJK radicals, Kangxi, CJK symbols and punctuation
        or >= 0x3041 and <= 0x33FF      // Kana, Hangul Compatibility Jamo, CJK compatibility
        or >= 0x3400 and <= 0x4DBF      // CJK Unified Ideographs Extension A
        or >= 0x4E00 and <= 0x9FFF      // CJK Unified Ideographs
        or >= 0xA960 and <= 0xA97F      // Hangul Jamo Extended-A
        or >= 0xAC00 and <= 0xD7A3      // Hangul syllables
        or >= 0xF900 and <= 0xFAFF      // CJK compatibility ideographs
        or >= 0xFE30 and <= 0xFE6F      // CJK compatibility forms
        or >= 0xFF00 and <= 0xFF60      // Fullwidth forms
        or >= 0xFFE0 and <= 0xFFE6
        or >= 0x1F300 and <= 0x1F64F    // Emoji
        or >= 0x20000 and <= 0x3FFFD;   // CJK Unified Ideographs Extension B and beyond
}
