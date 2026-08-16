using System.Buffers.Binary;
using System.Text;

namespace BetterTranslator.Packer;

internal static class PortableExecutableEditor
{
    private const int DosSignature = 0x5A4D;
    private const uint NtSignature = 0x00004550;
    private const ushort Pe32PlusMagic = 0x020B;
    private const int SectionHeaderBytes = 40;
    private const int SectionNameBytes = 8;
    private const uint InitializedDataReadOnly = 0x40000040;
    private const int CertificateDirectoryIndex = 4;

    public static byte[] AppendPayloadSection(byte[] image, string sectionName, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Encoding.ASCII.GetByteCount(sectionName), SectionNameBytes);

        Span<byte> file = image;

        if (BinaryPrimitives.ReadUInt16LittleEndian(file) != DosSignature)
        {
            throw new InvalidDataException("the bootstrap is not a DOS executable");
        }

        int ntOffset = BinaryPrimitives.ReadInt32LittleEndian(file[0x3C..]);
        if (ntOffset <= 0 || ntOffset + 24 > file.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(file[ntOffset..]) != NtSignature)
        {
            throw new InvalidDataException("the bootstrap has no PE signature");
        }

        int fileHeaderOffset = ntOffset + 4;
        ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(file[(fileHeaderOffset + 2)..]);
        ushort optionalHeaderBytes = BinaryPrimitives.ReadUInt16LittleEndian(file[(fileHeaderOffset + 16)..]);

        int optionalHeaderOffset = fileHeaderOffset + 20;
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[optionalHeaderOffset..]) != Pe32PlusMagic)
        {
            throw new InvalidDataException("the bootstrap is not a 64 bit PE image");
        }

        uint sectionAlignment = BinaryPrimitives.ReadUInt32LittleEndian(file[(optionalHeaderOffset + 32)..]);
        uint fileAlignment = BinaryPrimitives.ReadUInt32LittleEndian(file[(optionalHeaderOffset + 36)..]);
        uint headerBytes = BinaryPrimitives.ReadUInt32LittleEndian(file[(optionalHeaderOffset + 60)..]);
        uint directoryCount = BinaryPrimitives.ReadUInt32LittleEndian(file[(optionalHeaderOffset + 108)..]);

        if (sectionAlignment == 0 || fileAlignment == 0)
        {
            throw new InvalidDataException("the bootstrap declares a zero alignment");
        }

        int sectionTableOffset = optionalHeaderOffset + optionalHeaderBytes;
        int nextHeaderOffset = sectionTableOffset + (sectionCount * SectionHeaderBytes);

        if (nextHeaderOffset + SectionHeaderBytes > headerBytes)
        {
            throw new InvalidDataException(
                "the bootstrap has no room in its header for another section; link it with a larger /FILEALIGN header area");
        }

        uint nextVirtualAddress = 0;
        uint nextRawOffset = 0;

        for (int index = 0; index < sectionCount; index++)
        {
            int header = sectionTableOffset + (index * SectionHeaderBytes);
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(file[(header + 8)..]);
            uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(file[(header + 12)..]);
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(file[(header + 16)..]);
            uint rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(file[(header + 20)..]);

            if (Encoding.ASCII.GetString(file.Slice(header, SectionNameBytes)).TrimEnd('\0') == sectionName)
            {
                throw new InvalidDataException($"the bootstrap already carries a {sectionName} section");
            }

            nextVirtualAddress = Math.Max(nextVirtualAddress, Align(virtualAddress + virtualSize, sectionAlignment));
            nextRawOffset = Math.Max(nextRawOffset, Align(rawOffset + rawSize, fileAlignment));
        }

        if (nextRawOffset != image.Length)
        {
            throw new InvalidDataException(
                $"the bootstrap carries {image.Length - nextRawOffset} bytes of overlay data after its last section");
        }

        if (directoryCount > CertificateDirectoryIndex)
        {
            int certificate = optionalHeaderOffset + 112 + (CertificateDirectoryIndex * 8);
            if (BinaryPrimitives.ReadUInt32LittleEndian(file[certificate..]) != 0)
            {
                throw new InvalidDataException(
                    "the bootstrap is already signed; append the payload section before signing");
            }
        }

        uint rawPayloadBytes = Align((uint)payload.Length, fileAlignment);
        byte[] output = new byte[image.Length + rawPayloadBytes];
        image.CopyTo(output, 0);
        payload.CopyTo(output.AsSpan(image.Length));

        Span<byte> result = output;
        Span<byte> header8 = result.Slice(nextHeaderOffset, SectionHeaderBytes);
        header8.Clear();
        Encoding.ASCII.GetBytes(sectionName).CopyTo(header8);
        BinaryPrimitives.WriteUInt32LittleEndian(header8[8..], (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header8[12..], nextVirtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(header8[16..], rawPayloadBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header8[20..], nextRawOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header8[36..], InitializedDataReadOnly);

        BinaryPrimitives.WriteUInt16LittleEndian(result[(fileHeaderOffset + 2)..], (ushort)(sectionCount + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(result[(optionalHeaderOffset + 56)..],
                                                 Align(nextVirtualAddress + (uint)payload.Length, sectionAlignment));

        int checksumOffset = optionalHeaderOffset + 64;
        BinaryPrimitives.WriteUInt32LittleEndian(result[checksumOffset..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result[checksumOffset..], ComputeChecksum(result, checksumOffset));

        return output;
    }

    public static uint ComputeChecksum(ReadOnlySpan<byte> image, int checksumOffset)
    {
        ulong sum = 0;

        for (int offset = 0; offset + 1 < image.Length; offset += 2)
        {
            ushort word = offset == checksumOffset || offset == checksumOffset + 2
                ? (ushort)0
                : BinaryPrimitives.ReadUInt16LittleEndian(image[offset..]);

            sum += word;
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        if ((image.Length & 1) != 0)
        {
            sum += image[^1];
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        sum = (sum >> 16) + (sum & 0xFFFF);
        sum = (sum >> 16) + (sum & 0xFFFF);

        return (uint)(sum & 0xFFFF) + (uint)image.Length;
    }

    private static uint Align(uint value, uint alignment)
    {
        uint remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }
}
