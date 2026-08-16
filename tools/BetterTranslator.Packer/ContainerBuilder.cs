using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BetterTranslator.Packer;

internal sealed record PayloadFile(string PortablePath, string FullPath, long Length, uint Attributes);

internal sealed record ContainerStatistics(
    int EntryCount,
    int BlockCount,
    long UncompressedBytes,
    long ContainerBytes,
    uint Algorithm,
    string HashHex);

internal sealed record ContainerResult(byte[] Bytes, ContainerStatistics Statistics);

internal static class ContainerBuilder
{
    private sealed record CompressedBlock(byte[] Data, uint UncompressedSize, uint Flags);

    private sealed record CompressedEntry(PayloadFile File, byte[] ContentHash, List<CompressedBlock> Blocks);

    public static void AssertLayoutParity()
    {
        Verify<BtPayHeader>(BtPayFormat.HeaderBytes);
        Verify<BtPayEntry>(BtPayFormat.EntryFixedBytes);
        Verify<BtPayBlock>(BtPayFormat.BlockBytes);
        Verify<BtPayFooter>(BtPayFormat.FooterBytes);

        static void Verify<T>(uint expected) where T : unmanaged
        {
            int actual = Unsafe.SizeOf<T>();
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name} is {actual} bytes in C# but {expected} bytes in btpay_format.h");
            }
        }
    }

    public static ContainerResult Build(IReadOnlyList<PayloadFile> files, uint algorithm, uint blockSize,
                                        int degreeOfParallelism)
    {
        AssertLayoutParity();

        if (blockSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        }

        PayloadFile[] ordered = [.. files.OrderBy(file => file.PortablePath, StringComparer.Ordinal)];
        var compressed = new CompressedEntry[ordered.Length];

        Parallel.For(0, ordered.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degreeOfParallelism) },
            () => new WindowsCompressor(algorithm, blockSize),
            (index, _, compressor) =>
            {
                compressed[index] = CompressEntry(ordered[index], compressor, blockSize);
                return compressor;
            },
            compressor => compressor.Dispose());

        return Assemble(compressed, algorithm, blockSize);
    }

    private static CompressedEntry CompressEntry(PayloadFile file, WindowsCompressor compressor, uint blockSize)
    {
        byte[] contents = File.ReadAllBytes(file.FullPath);
        if (contents.LongLength != file.Length)
        {
            throw new IOException($"{file.FullPath} changed size while the container was being built");
        }

        byte[] contentHash = SHA256.HashData(contents);
        var blocks = new List<CompressedBlock>();

        for (long offset = 0; offset < contents.LongLength; offset += blockSize)
        {
            int slice = (int)Math.Min(blockSize, contents.LongLength - offset);
            ReadOnlySpan<byte> source = contents.AsSpan(checked((int)offset), slice);
            byte[] packed = compressor.Compress(source);

            blocks.Add(packed.Length < slice
                ? new CompressedBlock(packed, (uint)slice, 0)
                : new CompressedBlock(source.ToArray(), (uint)slice, BtPayFormat.BlockFlagStored));
        }

        return new CompressedEntry(file, contentHash, blocks);
    }

    private static unsafe ContainerResult Assemble(CompressedEntry[] entries, uint algorithm, uint blockSize)
    {
        long entryTableOffset = BtPayFormat.HeaderBytes;
        long entryTableBytes = 0;
        int blockCount = 0;
        long uncompressedBytes = 0;

        foreach (CompressedEntry entry in entries)
        {
            entryTableBytes += EntryRecordBytes(entry.File.PortablePath);
            blockCount += entry.Blocks.Count;
            uncompressedBytes += entry.File.Length;
        }

        long blockTableOffset = Align(entryTableOffset + entryTableBytes);
        long blockDataOffset = Align(blockTableOffset + ((long)blockCount * BtPayFormat.BlockBytes));

        long blockDataBytes = 0;
        foreach (CompressedEntry entry in entries)
        {
            foreach (CompressedBlock block in entry.Blocks)
            {
                blockDataBytes += block.Data.Length;
            }
        }

        long footerOffset = Align(blockDataOffset + blockDataBytes);
        long containerBytes = footerOffset + BtPayFormat.FooterBytes;

        byte[] container = new byte[checked((int)containerBytes)];
        Span<byte> buffer = container;

        var header = new BtPayHeader
        {
            Magic = BtPayFormat.ContainerMagic,
            FormatVersion = BtPayFormat.FormatVersion,
            Algorithm = algorithm,
            EntryCount = (uint)entries.Length,
            BlockCount = (uint)blockCount,
            BlockSize = blockSize,
            Flags = 0,
            EntryTableOffset = (ulong)entryTableOffset,
            EntryTableBytes = (ulong)entryTableBytes,
            BlockTableOffset = (ulong)blockTableOffset,
            BlockDataOffset = (ulong)blockDataOffset,
            BlockDataBytes = (ulong)blockDataBytes,
            FooterOffset = (ulong)footerOffset,
            TotalUncompressedSize = (ulong)uncompressedBytes,
            Reserved = 0,
        };

        MemoryMarshal.Write(buffer, in header);

        long entryCursor = entryTableOffset;
        long blockCursor = blockTableOffset;
        long dataCursor = 0;
        uint blockIndex = 0;

        foreach (CompressedEntry entry in entries)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(entry.File.PortablePath);

            var record = new BtPayEntry
            {
                UncompressedSize = (ulong)entry.File.Length,
                CompressedSize = (ulong)entry.Blocks.Sum(block => (long)block.Data.Length),
                DataOffset = (ulong)dataCursor,
                FirstBlock = blockIndex,
                BlockCount = (uint)entry.Blocks.Count,
                Attributes = entry.File.Attributes & BtPayFormat.AttributeMask,
                PathBytes = (uint)pathBytes.Length,
            };

            for (int index = 0; index < 32; index++)
            {
                record.ContentSha256[index] = entry.ContentHash[index];
            }

            MemoryMarshal.Write(buffer[checked((int)entryCursor)..], in record);
            entryCursor += BtPayFormat.EntryFixedBytes;

            pathBytes.CopyTo(buffer[checked((int)entryCursor)..]);
            entryCursor += pathBytes.Length;
            entryCursor = Align(entryCursor);

            foreach (CompressedBlock block in entry.Blocks)
            {
                var descriptor = new BtPayBlock
                {
                    DataOffset = (ulong)dataCursor,
                    CompressedSize = (uint)block.Data.Length,
                    UncompressedSize = block.UncompressedSize,
                    Flags = block.Flags,
                    Reserved = 0,
                };

                MemoryMarshal.Write(buffer[checked((int)blockCursor)..], in descriptor);
                blockCursor += BtPayFormat.BlockBytes;

                block.Data.CopyTo(buffer.Slice(checked((int)(blockDataOffset + dataCursor)), block.Data.Length));
                dataCursor += block.Data.Length;
                blockIndex++;
            }
        }

        byte[] payloadHash = SHA256.HashData(container.AsSpan(0, checked((int)footerOffset)));

        var footer = new BtPayFooter
        {
            Magic = BtPayFormat.FooterMagic,
            ContainerSize = (ulong)containerBytes,
        };

        for (int index = 0; index < 32; index++)
        {
            footer.PayloadSha256[index] = payloadHash[index];
        }

        MemoryMarshal.Write(buffer[checked((int)footerOffset)..], in footer);

        var statistics = new ContainerStatistics(entries.Length, blockCount, uncompressedBytes, containerBytes,
                                                 algorithm, Convert.ToHexStringLower(payloadHash));

        return new ContainerResult(container, statistics);
    }

    private static long EntryRecordBytes(string portablePath) =>
        Align(BtPayFormat.EntryFixedBytes + Encoding.UTF8.GetByteCount(portablePath));

    private static long Align(long value)
    {
        long alignment = BtPayFormat.TableAlignment;
        long remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }
}
