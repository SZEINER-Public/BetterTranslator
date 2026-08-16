using System.Buffers.Binary;
using System.Text;

namespace BetterTranslator.Packer;

internal sealed record PortableExecutableSection(string Name, uint VirtualAddress, uint VirtualSize,
                                                 uint RawOffset, uint RawSize);

internal static class PortableExecutableReader
{
    private const int SectionHeaderBytes = 40;
    private const int SectionNameBytes = 8;

    public static IReadOnlyList<PortableExecutableSection> ReadSections(ReadOnlySpan<byte> image)
    {
        int ntOffset = BinaryPrimitives.ReadInt32LittleEndian(image[0x3C..]);
        int fileHeaderOffset = ntOffset + 4;
        ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image[(fileHeaderOffset + 2)..]);
        ushort optionalHeaderBytes = BinaryPrimitives.ReadUInt16LittleEndian(image[(fileHeaderOffset + 16)..]);
        int sectionTableOffset = fileHeaderOffset + 20 + optionalHeaderBytes;

        var sections = new List<PortableExecutableSection>(sectionCount);

        for (int index = 0; index < sectionCount; index++)
        {
            int header = sectionTableOffset + (index * SectionHeaderBytes);
            sections.Add(new PortableExecutableSection(
                Encoding.ASCII.GetString(image.Slice(header, SectionNameBytes)).TrimEnd('\0'),
                BinaryPrimitives.ReadUInt32LittleEndian(image[(header + 12)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(image[(header + 8)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(image[(header + 20)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(image[(header + 16)..])));
        }

        return sections;
    }

    public static byte[] ReadSection(byte[] image, string sectionName)
    {
        PortableExecutableSection section = ReadSections(image).FirstOrDefault(s => s.Name == sectionName)
            ?? throw new InvalidDataException($"the image has no {sectionName} section");

        return image.AsSpan(checked((int)section.RawOffset), checked((int)section.VirtualSize)).ToArray();
    }

    public static uint ReadChecksum(ReadOnlySpan<byte> image)
    {
        int ntOffset = BinaryPrimitives.ReadInt32LittleEndian(image[0x3C..]);
        return BinaryPrimitives.ReadUInt32LittleEndian(image[(ntOffset + 4 + 20 + 64)..]);
    }

    public static int ChecksumOffset(ReadOnlySpan<byte> image)
    {
        int ntOffset = BinaryPrimitives.ReadInt32LittleEndian(image[0x3C..]);
        return ntOffset + 4 + 20 + 64;
    }
}
