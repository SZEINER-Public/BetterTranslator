using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BetterTranslator.Tests;

/// <summary>
/// Writes a file that is genuinely the length a catalogue entry declares
/// without spending the disk.
///
/// Installed state is decided on the byte length now, which is the whole point:
/// a short file is a partial download and must not pass. A fixture standing in
/// for a 5.2 GB model therefore has to be 5.2 GB long, and the only way to have
/// that in a test is a sparse file. The extent is never allocated; reads return
/// zeros and the length is real.
/// </summary>
internal static class SparseArtifact
{
    private const uint SetSparse = 0x000900C4;

    public static void Write(string path, long length, byte[]? header = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        MarkSparse(stream.SafeFileHandle);

        if (header is { Length: > 0 })
        {
            stream.Write(header);
        }

        stream.SetLength(length);
    }

    /// <summary>
    /// Best effort. A volume that does not support sparse extents answers with
    /// an error and the file is simply allocated, which is correct if slower;
    /// the temp volume on Windows is NTFS and does support it.
    /// </summary>
    private static void MarkSparse(SafeFileHandle handle) =>
        DeviceIoControl(handle, SetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
