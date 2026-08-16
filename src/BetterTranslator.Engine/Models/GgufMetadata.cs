using System.Buffers.Binary;
using System.Text;

namespace BetterTranslator.Engine.Models;

/// <summary>
/// Reads the metadata header of a GGUF file.
///
/// Written because the inference ABI exposes no way to ask a model how it wants
/// to be addressed -- eighteen exported symbols, none of them a tokenizer or a
/// template accessor -- while the file itself carries the answer in
/// `tokenizer.chat_template`. Without it every model is prompted with the one
/// template compiled into the runtime, which is right for exactly one of them.
///
/// Only the header is read. The tensor data is never touched, so this costs a
/// few kilobytes off the front of a file that may be fourteen gigabytes.
/// </summary>
public static class GgufMetadata
{
    /// <summary>"GGUF", little-endian.</summary>
    private const uint Magic = 0x46554747;

    /// <summary>
    /// v1 used 32-bit lengths and is long extinct; v2 and v3 use 64-bit. A file
    /// claiming anything else is not read rather than guessed at.
    /// </summary>
    private const uint MinVersion = 2;

    private const uint MaxVersion = 3;

    /// <summary>
    /// Sanity bounds. A corrupt or truncated header would otherwise ask for an
    /// allocation the size of whatever the bytes happened to say.
    /// </summary>
    private const ulong MaxKeyValueCount = 1_000_000;

    private const ulong MaxStringBytes = 64L * 1024 * 1024;

    public const string ChatTemplateKey = "tokenizer.chat_template";

    public const string ArchitectureKey = "general.architecture";

    public const string NameKey = "general.name";

    /// <summary>
    /// The chat template a model declares, or null when the file does not carry
    /// one, is not a GGUF, or cannot be read.
    ///
    /// Null is not an error worth raising: a model with no template is a real
    /// case, and the caller falls back to the runtime's own prompt.
    /// </summary>
    public static string? ChatTemplate(string path) => ReadString(path, ChatTemplateKey);

    public static string? Architecture(string path) => ReadString(path, ArchitectureKey);

    /// <summary>
    /// Reads one string-valued metadata key. Values that are not strings are
    /// skipped rather than converted, because every key this needs is a string
    /// and guessing at the rest would be a parser nobody asked for.
    /// </summary>
    public static string? ReadString(string path, string key)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            if (reader.ReadUInt32() != Magic)
            {
                return null;
            }

            var version = reader.ReadUInt32();

            if (version is < MinVersion or > MaxVersion)
            {
                return null;
            }

            _ = reader.ReadUInt64();                 // tensor count, not needed
            var count = reader.ReadUInt64();

            if (count > MaxKeyValueCount)
            {
                return null;
            }

            for (ulong i = 0; i < count; i++)
            {
                var name = ReadGgufString(reader);

                if (name is null)
                {
                    return null;
                }

                var type = (GgufType)reader.ReadUInt32();

                if (type == GgufType.String && string.Equals(name, key, StringComparison.Ordinal))
                {
                    return ReadGgufString(reader);
                }

                if (!SkipValue(reader, type))
                {
                    return null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            // Truncated, locked, or not a GGUF at all. The caller falls back.
            return null;
        }

        return null;
    }

    private static string? ReadGgufString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();

        if (length > MaxStringBytes)
        {
            return null;
        }

        var bytes = reader.ReadBytes((int)length);

        return bytes.Length == (int)length ? Encoding.UTF8.GetString(bytes) : null;
    }

    /// <summary>
    /// Steps over a value without materialising it. The token list in a real
    /// model is a quarter of a million strings; reading it to reach the key
    /// after it would defeat the point of reading only the header.
    /// </summary>
    private static bool SkipValue(BinaryReader reader, GgufType type)
    {
        switch (type)
        {
            case GgufType.UInt8 or GgufType.Int8 or GgufType.Bool:
                reader.BaseStream.Seek(1, SeekOrigin.Current);
                return true;

            case GgufType.UInt16 or GgufType.Int16:
                reader.BaseStream.Seek(2, SeekOrigin.Current);
                return true;

            case GgufType.UInt32 or GgufType.Int32 or GgufType.Float32:
                reader.BaseStream.Seek(4, SeekOrigin.Current);
                return true;

            case GgufType.UInt64 or GgufType.Int64 or GgufType.Float64:
                reader.BaseStream.Seek(8, SeekOrigin.Current);
                return true;

            case GgufType.String:
            {
                var length = reader.ReadUInt64();

                if (length > MaxStringBytes)
                {
                    return false;
                }

                reader.BaseStream.Seek((long)length, SeekOrigin.Current);
                return true;
            }

            case GgufType.Array:
            {
                var element = (GgufType)reader.ReadUInt32();
                var length = reader.ReadUInt64();

                if (element == GgufType.String)
                {
                    // Each one carries its own length, so they have to be walked.
                    for (ulong i = 0; i < length; i++)
                    {
                        var size = reader.ReadUInt64();

                        if (size > MaxStringBytes)
                        {
                            return false;
                        }

                        reader.BaseStream.Seek((long)size, SeekOrigin.Current);
                    }

                    return true;
                }

                var width = Width(element);

                if (width == 0)
                {
                    // A nested array, which the format allows and no real model
                    // uses. Refusing beats seeking to a position worked out from
                    // a guess.
                    return false;
                }

                reader.BaseStream.Seek((long)length * width, SeekOrigin.Current);
                return true;
            }

            default:
                return false;
        }
    }

    private static int Width(GgufType type) => type switch
    {
        GgufType.UInt8 or GgufType.Int8 or GgufType.Bool => 1,
        GgufType.UInt16 or GgufType.Int16 => 2,
        GgufType.UInt32 or GgufType.Int32 or GgufType.Float32 => 4,
        GgufType.UInt64 or GgufType.Int64 or GgufType.Float64 => 8,
        _ => 0,
    };

    private enum GgufType : uint
    {
        UInt8 = 0,
        Int8 = 1,
        UInt16 = 2,
        Int16 = 3,
        UInt32 = 4,
        Int32 = 5,
        Float32 = 6,
        Bool = 7,
        String = 8,
        Array = 9,
        UInt64 = 10,
        Int64 = 11,
        Float64 = 12,
    }
}
