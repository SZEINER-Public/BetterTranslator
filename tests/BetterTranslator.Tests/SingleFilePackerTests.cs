using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BetterTranslator.Packer;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class SingleFilePackerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "bt-packer-" + Guid.NewGuid().ToString("n"));

    public SingleFilePackerTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void TheGeneratedStructsMatchTheSizesDeclaredInTheSharedHeader()
    {
        ContainerBuilder.AssertLayoutParity();

        Unsafe.SizeOf<BtPayHeader>().Should().Be((int)BtPayFormat.HeaderBytes);
        Unsafe.SizeOf<BtPayEntry>().Should().Be((int)BtPayFormat.EntryFixedBytes);
        Unsafe.SizeOf<BtPayBlock>().Should().Be((int)BtPayFormat.BlockBytes);
        Unsafe.SizeOf<BtPayFooter>().Should().Be((int)BtPayFormat.FooterBytes);
    }

    [Theory]
    [InlineData(BtPayFormat.AlgorithmStore)]
    [InlineData(BtPayFormat.AlgorithmXpressHuff)]
    [InlineData(BtPayFormat.AlgorithmLzms)]
    public void TheRoundTripPreservesNestedPathsEmptyFilesNonAsciiNamesAndAttributes(uint algorithm)
    {
        var written = BuildEdgeCaseTree();
        var files = PayloadInventory.Collect(root, ["*.excluded"]);

        ContainerResult container = ContainerBuilder.Build(files, algorithm, BtPayFormat.DefaultBlockSize, 4);
        ContainerReader reader = ContainerReader.Open(container.Bytes);

        reader.Entries.Select(entry => entry.PortablePath)
              .Should().BeEquivalentTo(written.Keys);

        foreach (ContainerEntry entry in reader.Entries)
        {
            reader.Expand(entry).Should().Equal(written[entry.PortablePath],
                                                $"{entry.PortablePath} must survive the round trip");
        }

        ContainerEntry readOnly = reader.Entries.Single(entry => entry.PortablePath == "nested/read-only.bin");
        (readOnly.Attributes & BtPayFormat.AttributeReadOnly).Should().Be(BtPayFormat.AttributeReadOnly);

        reader.Entries.Single(entry => entry.PortablePath == "nested/deeper/empty.txt")
              .UncompressedSize.Should().Be(0);

        reader.Entries.Should().Contain(entry => entry.PortablePath == "nested/deeper/príliš-žluťoučký-テスト.txt");
        reader.Entries.Should().NotContain(entry => entry.PortablePath.EndsWith(".excluded"));
    }

    [Fact]
    public void TheEntryTableIsOrderedByOrdinalPathSoTwoRunsAgree()
    {
        BuildEdgeCaseTree();
        var files = PayloadInventory.Collect(root, []);

        ContainerReader reader = ContainerReader.Open(
            ContainerBuilder.Build(files, BtPayFormat.AlgorithmStore, BtPayFormat.DefaultBlockSize, 4).Bytes);

        reader.Entries.Select(entry => entry.PortablePath)
              .Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void TwoRunsOverTheSameTreeProduceByteIdenticalContainers()
    {
        BuildEdgeCaseTree();
        var files = PayloadInventory.Collect(root, []);

        byte[] first = ContainerBuilder.Build(files, BtPayFormat.AlgorithmLzms, BtPayFormat.DefaultBlockSize, 4).Bytes;
        byte[] second = ContainerBuilder.Build(files, BtPayFormat.AlgorithmLzms, BtPayFormat.DefaultBlockSize, 1).Bytes;

        second.Should().Equal(first);
    }

    [Fact]
    public void EveryContainerFieldThatCarriesASizeIsWideEnoughForMoreThanTwoGigabytes()
    {
        const ulong beyondFourGigabytes = 5_000_000_000UL;

        var header = new BtPayHeader
        {
            Magic = BtPayFormat.ContainerMagic,
            TotalUncompressedSize = beyondFourGigabytes,
            BlockDataOffset = beyondFourGigabytes,
            FooterOffset = beyondFourGigabytes + 17,
        };

        byte[] buffer = new byte[BtPayFormat.HeaderBytes];
        MemoryMarshal.Write(buffer.AsSpan(), in header);
        BtPayHeader restored = MemoryMarshal.Read<BtPayHeader>(buffer);

        restored.TotalUncompressedSize.Should().Be(beyondFourGigabytes);
        restored.BlockDataOffset.Should().Be(beyondFourGigabytes);
        restored.FooterOffset.Should().Be(beyondFourGigabytes + 17);

        var entry = new BtPayEntry { UncompressedSize = beyondFourGigabytes, DataOffset = beyondFourGigabytes };
        byte[] entryBuffer = new byte[BtPayFormat.EntryFixedBytes];
        MemoryMarshal.Write(entryBuffer.AsSpan(), in entry);

        BtPayEntry restoredEntry = MemoryMarshal.Read<BtPayEntry>(entryBuffer);
        restoredEntry.UncompressedSize.Should().Be(beyondFourGigabytes);
        restoredEntry.DataOffset.Should().Be(beyondFourGigabytes);
    }

    [Fact]
    public void ACorruptedContainerByteIsRejectedByTheFooterHash()
    {
        BuildEdgeCaseTree();
        var files = PayloadInventory.Collect(root, []);

        byte[] container = ContainerBuilder.Build(files, BtPayFormat.AlgorithmStore, BtPayFormat.DefaultBlockSize, 2).Bytes;
        container[(int)BtPayFormat.HeaderBytes + 40] ^= 0xFF;

        Action open = () => ContainerReader.Open(container);
        open.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ThePackedImageCarriesThePayloadInItsOwnSectionWithACorrectChecksum()
    {
        BuildEdgeCaseTree();
        var files = PayloadInventory.Collect(root, []);
        byte[] container = ContainerBuilder.Build(files, BtPayFormat.AlgorithmStore, BtPayFormat.DefaultBlockSize, 2).Bytes;

        byte[] host = SyntheticHostImage();
        byte[] packed = PortableExecutableEditor.AppendPayloadSection(host, ".btpay", container);

        PortableExecutableReader.ReadSection(packed, ".btpay").Should().Equal(container);

        uint stored = PortableExecutableReader.ReadChecksum(packed);
        stored.Should().Be(PortableExecutableEditor.ComputeChecksum(packed,
                                                                   PortableExecutableReader.ChecksumOffset(packed)));

        ContainerReader.Open(ContainerReader.ExtractSection(packed, ".btpay"))
                       .Entries.Should().HaveCount(files.Count);
    }

    [Fact]
    public void AppendingASecondPayloadSectionIsRefused()
    {
        byte[] host = SyntheticHostImage();
        byte[] once = PortableExecutableEditor.AppendPayloadSection(host, ".btpay", new byte[64]);

        Action twice = () => PortableExecutableEditor.AppendPayloadSection(once, ".btpay", new byte[64]);
        twice.Should().Throw<InvalidDataException>().WithMessage("*already carries*");
    }

    private Dictionary<string, byte[]> BuildEdgeCaseTree()
    {
        var written = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        Add("root.txt", Encoding.UTF8.GetBytes("BetterTranslator"));
        Add("nested/deeper/empty.txt", []);
        Add("nested/deeper/príliš-žluťoučký-テスト.txt",
            Encoding.UTF8.GetBytes("non-ascii テスト"));
        Add("nested/large.bin", RandomNumberGenerator.GetBytes(1_500_000));
        Add("nested/compressible.bin", Enumerable.Repeat((byte)0x5A, 900_000).ToArray());
        Add("nested/read-only.bin", Encoding.UTF8.GetBytes("locked"));
        Add("skipped.excluded", Encoding.UTF8.GetBytes("never packed"));

        File.SetAttributes(Path.Combine(root, "nested", "read-only.bin"), FileAttributes.ReadOnly);
        written.Remove("skipped.excluded");

        return written;

        void Add(string relative, byte[] contents)
        {
            string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, contents);
            written[relative] = contents;
        }
    }

    private static byte[] SyntheticHostImage()
    {
        const int ntOffset = 0x80;
        const int optionalHeaderBytes = 240;
        const int headerBytes = 0x400;
        const int fileAlignment = 0x200;
        const int sectionAlignment = 0x1000;

        byte[] image = new byte[headerBytes + fileAlignment];

        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BitConverter.GetBytes(ntOffset).CopyTo(image, 0x3C);
        BitConverter.GetBytes(0x00004550u).CopyTo(image, ntOffset);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(image, ntOffset + 4);
        BitConverter.GetBytes((ushort)1).CopyTo(image, ntOffset + 6);
        BitConverter.GetBytes((ushort)optionalHeaderBytes).CopyTo(image, ntOffset + 20);

        int optional = ntOffset + 24;
        BitConverter.GetBytes((ushort)0x020B).CopyTo(image, optional);
        BitConverter.GetBytes((uint)sectionAlignment).CopyTo(image, optional + 32);
        BitConverter.GetBytes((uint)fileAlignment).CopyTo(image, optional + 36);
        BitConverter.GetBytes((uint)(sectionAlignment * 2)).CopyTo(image, optional + 56);
        BitConverter.GetBytes((uint)headerBytes).CopyTo(image, optional + 60);
        BitConverter.GetBytes(16u).CopyTo(image, optional + 108);

        int sectionTable = optional + optionalHeaderBytes;
        Encoding.ASCII.GetBytes(".text").CopyTo(image, sectionTable);
        BitConverter.GetBytes((uint)fileAlignment).CopyTo(image, sectionTable + 8);
        BitConverter.GetBytes((uint)sectionAlignment).CopyTo(image, sectionTable + 12);
        BitConverter.GetBytes((uint)fileAlignment).CopyTo(image, sectionTable + 16);
        BitConverter.GetBytes((uint)headerBytes).CopyTo(image, sectionTable + 20);
        BitConverter.GetBytes(0x60000020u).CopyTo(image, sectionTable + 36);

        return image;
    }
}
