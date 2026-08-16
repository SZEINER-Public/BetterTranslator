using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BetterTranslator.Packer;

internal sealed record ContainerEntry(
    string PortablePath,
    long UncompressedSize,
    long CompressedSize,
    uint FirstBlock,
    uint BlockCount,
    uint Attributes,
    byte[] ContentSha256);

internal sealed class ContainerReader
{
    private readonly byte[] container;
    private readonly BtPayHeader header;
    private readonly BtPayBlock[] blocks;

    private ContainerReader(byte[] bytes, BtPayHeader containerHeader, BtPayBlock[] blockTable,
                            IReadOnlyList<ContainerEntry> entries)
    {
        container = bytes;
        header = containerHeader;
        blocks = blockTable;
        Entries = entries;
    }

    public IReadOnlyList<ContainerEntry> Entries { get; }

    public uint Algorithm => header.Algorithm;

    public uint BlockSize => header.BlockSize;

    public long TotalUncompressedSize => (long)header.TotalUncompressedSize;

    public string HashHex { get; private init; } = string.Empty;

    public static unsafe ContainerReader Open(byte[] bytes)
    {
        ContainerBuilder.AssertLayoutParity();

        BtPayHeader header = MemoryMarshal.Read<BtPayHeader>(bytes);
        if (header.Magic != BtPayFormat.ContainerMagic)
        {
            throw new InvalidDataException("the container magic does not match");
        }

        if (header.FormatVersion != BtPayFormat.FormatVersion)
        {
            throw new InvalidDataException($"container format version {header.FormatVersion} is not supported");
        }

        BtPayFooter footer = MemoryMarshal.Read<BtPayFooter>(bytes.AsSpan(checked((int)header.FooterOffset)));
        if (footer.Magic != BtPayFormat.FooterMagic)
        {
            throw new InvalidDataException("the footer magic does not match");
        }

        if (footer.ContainerSize != header.FooterOffset + BtPayFormat.FooterBytes)
        {
            throw new InvalidDataException("the footer reports an inconsistent container size");
        }

        byte[] recorded = new byte[32];
        for (int index = 0; index < 32; index++)
        {
            recorded[index] = footer.PayloadSha256[index];
        }

        byte[] computed = SHA256.HashData(bytes.AsSpan(0, checked((int)header.FooterOffset)));
        if (!computed.AsSpan().SequenceEqual(recorded))
        {
            throw new InvalidDataException("the container hash does not match its footer");
        }

        var blocks = new BtPayBlock[header.BlockCount];
        for (int index = 0; index < blocks.Length; index++)
        {
            int offset = checked((int)(header.BlockTableOffset + ((ulong)index * BtPayFormat.BlockBytes)));
            blocks[index] = MemoryMarshal.Read<BtPayBlock>(bytes.AsSpan(offset));
        }

        var entries = new List<ContainerEntry>(checked((int)header.EntryCount));
        long cursor = (long)header.EntryTableOffset;

        for (uint index = 0; index < header.EntryCount; index++)
        {
            BtPayEntry record = MemoryMarshal.Read<BtPayEntry>(bytes.AsSpan(checked((int)cursor)));
            cursor += BtPayFormat.EntryFixedBytes;

            string path = Encoding.UTF8.GetString(bytes, checked((int)cursor), checked((int)record.PathBytes));
            cursor += record.PathBytes;
            cursor = Align(cursor);

            byte[] contentHash = new byte[32];
            for (int position = 0; position < 32; position++)
            {
                contentHash[position] = record.ContentSha256[position];
            }

            entries.Add(new ContainerEntry(path, (long)record.UncompressedSize, (long)record.CompressedSize,
                                           record.FirstBlock, record.BlockCount, record.Attributes, contentHash));
        }

        return new ContainerReader(bytes, header, blocks, entries)
        {
            HashHex = Convert.ToHexStringLower(recorded),
        };
    }

    public byte[] Expand(ContainerEntry entry)
    {
        using var decompressor = new WindowsDecompressor(header.Algorithm);

        byte[] output = new byte[entry.UncompressedSize];
        int offset = 0;

        for (uint index = 0; index < entry.BlockCount; index++)
        {
            BtPayBlock block = blocks[entry.FirstBlock + index];
            int source = checked((int)(header.BlockDataOffset + block.DataOffset));
            ReadOnlySpan<byte> compressed = container.AsSpan(source, checked((int)block.CompressedSize));
            Span<byte> destination = output.AsSpan(offset, checked((int)block.UncompressedSize));

            if ((block.Flags & BtPayFormat.BlockFlagStored) != 0)
            {
                compressed.CopyTo(destination);
            }
            else
            {
                decompressor.Expand(compressed, destination);
            }

            offset += checked((int)block.UncompressedSize);
        }

        if (offset != output.Length)
        {
            throw new InvalidDataException($"{entry.PortablePath} expanded to {offset} of {output.Length} bytes");
        }

        if (!SHA256.HashData(output).AsSpan().SequenceEqual(entry.ContentSha256))
        {
            throw new InvalidDataException($"{entry.PortablePath} failed its content hash");
        }

        return output;
    }

    public static byte[] ExtractSection(byte[] image, string sectionName) =>
        PortableExecutableReader.ReadSection(image, sectionName);

    private static long Align(long value)
    {
        long remainder = value % BtPayFormat.TableAlignment;
        return remainder == 0 ? value : value + (BtPayFormat.TableAlignment - remainder);
    }
}
