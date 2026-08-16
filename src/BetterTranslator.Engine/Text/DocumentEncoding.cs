using System.Text;

namespace BetterTranslator.Engine.Text;

public static class DocumentEncoding
{
    private static readonly byte[] Preamble = [0xEF, 0xBB, 0xBF];

    public static bool HasByteOrderMark(ReadOnlySpan<byte> head) =>
        head.Length >= 3 && head[0] == Preamble[0] && head[1] == Preamble[1] && head[2] == Preamble[2];

    public static bool ByteOrderMarkAt(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            using var stream = File.OpenRead(path);

            Span<byte> head = stackalloc byte[3];
            var read = stream.ReadAtLeast(head, 3, throwOnEndOfStream: false);

            return HasByteOrderMark(head[..read]);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool EmitsByteOrderMark(bool sourceHadOne) =>
        Config.PipelineOptions.PreserveByteOrderMark && sourceHadOne;

    public static byte[] Emit(string text, bool sourceHadOne)
    {
        ArgumentNullException.ThrowIfNull(text);

        var body = new UTF8Encoding(false).GetBytes(text);

        if (!EmitsByteOrderMark(sourceHadOne))
        {
            return body;
        }

        var bytes = new byte[Preamble.Length + body.Length];
        Preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, Preamble.Length);

        return bytes;
    }
}
