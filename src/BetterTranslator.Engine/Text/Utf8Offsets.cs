using System.Text;

namespace BetterTranslator.Engine.Text;

public sealed class Utf8Offsets
{
    private readonly int[] _charAtByte;

    private Utf8Offsets(int[] charAtByte)
    {
        _charAtByte = charAtByte;
    }

    public int ByteLength => _charAtByte.Length - 1;

    public static Utf8Offsets Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var byteLength = Encoding.UTF8.GetByteCount(text);
        var map = new int[byteLength + 1];
        var at = 0;

        for (var i = 0; i < text.Length;)
        {
            var width = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var bytes = Encoding.UTF8.GetByteCount(text.AsSpan(i, width));

            for (var b = 0; b < bytes; b++)
            {
                map[at + b] = i;
            }

            at += bytes;
            i += width;
        }

        map[byteLength] = text.Length;
        return new Utf8Offsets(map);
    }

    public int CharIndex(int byteOffset) =>
        _charAtByte[Math.Clamp(byteOffset, 0, _charAtByte.Length - 1)];

    public (int Start, int Length) CharRange(int byteStart, int byteLength)
    {
        var start = CharIndex(byteStart);
        var end = CharIndex(byteStart + byteLength);
        return (start, end - start);
    }
}
