using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace BetterTranslator.Tools.AppIcon;

internal static class IconWriter
{
    private const int PngThreshold = 256;

    public static void Write(string path, IReadOnlyList<(int Size, byte[] Pixels)> frames)
    {
        var payloads = frames
            .Select(frame => frame.Size >= PngThreshold
                ? EncodePng(frame.Size, frame.Pixels)
                : EncodeDeviceIndependentBitmap(frame.Size, frame.Pixels))
            .ToList();

        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Count);

        var offset = 6 + (16 * frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var size = frames[i].Size;

            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(payloads[i].Length);
            writer.Write(offset);

            offset += payloads[i].Length;
        }

        foreach (var payload in payloads)
        {
            writer.Write(payload);
        }
    }

    private static byte[] EncodeDeviceIndependentBitmap(int size, byte[] pixels)
    {
        var maskStride = ((size + 31) / 32) * 4;

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(size * size * 4);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        for (var y = size - 1; y >= 0; y--)
        {
            writer.Write(pixels, y * size * 4, size * 4);
        }

        for (var y = size - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            for (var x = 0; x < size; x++)
            {
                if (pixels[(((y * size) + x) * 4) + 3] < 128)
                {
                    row[x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }

            writer.Write(row);
        }

        writer.Flush();
        return buffer.ToArray();
    }

    private static byte[] EncodePng(int size, byte[] pixels)
    {
        var raw = new byte[size * ((size * 4) + 1)];
        var cursor = 0;
        for (var y = 0; y < size; y++)
        {
            raw[cursor++] = 0;
            for (var x = 0; x < size; x++)
            {
                var offset = ((y * size) + x) * 4;
                raw[cursor++] = pixels[offset + 2];
                raw[cursor++] = pixels[offset + 1];
                raw[cursor++] = pixels[offset];
                raw[cursor++] = pixels[offset + 3];
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8;
        header[9] = 6;

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);

        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typed = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
        {
            typed[i] = (byte)type[i];
        }

        data.CopyTo(typed, 4);
        stream.Write(typed);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
