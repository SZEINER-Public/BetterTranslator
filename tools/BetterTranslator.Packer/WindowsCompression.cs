using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BetterTranslator.Packer;

internal sealed partial class WindowsCompressor : IDisposable
{
    private const int BlockSizeInformationClass = 1;
    private const uint XpressHuffAlgorithm = 4;
    private const uint LzmsAlgorithm = 5;

    private readonly nint handle;
    private readonly uint algorithm;

    public WindowsCompressor(uint containerAlgorithm, uint blockSize)
    {
        algorithm = containerAlgorithm;

        if (containerAlgorithm == BtPayFormat.AlgorithmStore)
        {
            return;
        }

        uint native = containerAlgorithm switch
        {
            BtPayFormat.AlgorithmXpressHuff => XpressHuffAlgorithm,
            BtPayFormat.AlgorithmLzms => LzmsAlgorithm,
            _ => throw new NotSupportedException($"container algorithm {containerAlgorithm} is not supported"),
        };

        if (!CreateCompressor(native, 0, out handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateCompressor failed");
        }

        if (containerAlgorithm == BtPayFormat.AlgorithmLzms)
        {
            uint configured = blockSize;
            if (!SetCompressorInformation(handle, BlockSizeInformationClass, ref configured, sizeof(uint)))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetCompressorInformation failed");
            }
        }
    }

    public static uint ParseAlgorithm(string name) => name switch
    {
        "store" => BtPayFormat.AlgorithmStore,
        "xpress-huff" => BtPayFormat.AlgorithmXpressHuff,
        "lzms" => BtPayFormat.AlgorithmLzms,
        _ => throw new ArgumentException($"unknown algorithm '{name}', expected store, xpress-huff or lzms"),
    };

    public static string DescribeAlgorithm(uint value) => value switch
    {
        BtPayFormat.AlgorithmStore => "store",
        BtPayFormat.AlgorithmXpressHuff => "xpress-huff",
        BtPayFormat.AlgorithmLzms => "lzms",
        _ => value.ToString(),
    };

    public byte[] Compress(ReadOnlySpan<byte> source)
    {
        if (algorithm == BtPayFormat.AlgorithmStore || source.Length == 0)
        {
            return source.ToArray();
        }

        nuint produced = 0;
        byte[] scratch = new byte[source.Length + (source.Length / 8) + 4096];

        if (!Compress(handle, source, (nuint)source.Length, scratch, (nuint)scratch.Length, out produced))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != 122)
            {
                throw new Win32Exception(error, "Compress failed");
            }

            scratch = new byte[checked((int)produced)];
            if (!Compress(handle, source, (nuint)source.Length, scratch, (nuint)scratch.Length, out produced))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Compress failed");
            }
        }

        return scratch.AsSpan(0, checked((int)produced)).ToArray();
    }

    public void Dispose()
    {
        if (handle != 0)
        {
            CloseCompressor(handle);
        }
    }

    [LibraryImport("Cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateCompressor(uint algorithm, nint allocationRoutines, out nint handle);

    [LibraryImport("Cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCompressorInformation(nint handle, int informationClass,
                                                         ref uint information, nuint informationSize);

    [LibraryImport("Cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Compress(nint handle, ReadOnlySpan<byte> uncompressed, nuint uncompressedSize,
                                         Span<byte> compressed, nuint compressedSize, out nuint compressedUsed);

    [LibraryImport("Cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseCompressor(nint handle);
}

internal sealed partial class WindowsDecompressor : IDisposable
{
    private const uint XpressHuffAlgorithm = 4;
    private const uint LzmsAlgorithm = 5;

    private readonly nint handle;
    private readonly uint algorithm;

    public WindowsDecompressor(uint containerAlgorithm)
    {
        algorithm = containerAlgorithm;

        if (containerAlgorithm == BtPayFormat.AlgorithmStore)
        {
            return;
        }

        uint native = containerAlgorithm switch
        {
            BtPayFormat.AlgorithmXpressHuff => XpressHuffAlgorithm,
            BtPayFormat.AlgorithmLzms => LzmsAlgorithm,
            _ => throw new NotSupportedException($"container algorithm {containerAlgorithm} is not supported"),
        };

        if (!CreateDecompressor(native, 0, out handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateDecompressor failed");
        }
    }

    public void Expand(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (algorithm == BtPayFormat.AlgorithmStore)
        {
            source.CopyTo(destination);
            return;
        }

        if (!Decompress(handle, source, (nuint)source.Length, destination, (nuint)destination.Length,
                        out nuint produced))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Decompress failed");
        }

        if ((int)produced != destination.Length)
        {
            throw new InvalidDataException($"a block expanded to {produced} bytes, expected {destination.Length}");
        }
    }

    public void Dispose()
    {
        if (handle != 0)
        {
            CloseDecompressor(handle);
        }
    }

    [LibraryImport("Cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDecompressor(uint algorithm, nint allocationRoutines, out nint handle);

    [LibraryImport("Cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Decompress(nint handle, ReadOnlySpan<byte> compressed, nuint compressedSize,
                                           Span<byte> uncompressed, nuint uncompressedSize,
                                           out nuint uncompressedUsed);

    [LibraryImport("Cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDecompressor(nint handle);
}
