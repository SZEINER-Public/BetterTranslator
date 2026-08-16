namespace BetterTranslator.Runtime.Downloads;

/// <summary>What a downloaded artifact must turn out to be.</summary>
public sealed record ArtifactIntegrity
{
    public required string FileId { get; init; }

    public required string FileName { get; init; }

    /// <summary>
    /// Byte length from the server's own Content-Range. Known for every
    /// artifact, and the cheapest check that catches a truncated transfer.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Lowercase hex SHA-256, or null where no full read has produced one yet.
    ///
    /// Null is a stated gap, never a pass: <see cref="ArtifactManifest.Verify"/>
    /// reports it as unverifiable so a caller cannot mistake "nothing to compare
    /// against" for "compared and matched".
    /// </summary>
    public string? Sha256 { get; init; }
}

/// <summary>Outcome of checking a completed file against the manifest.</summary>
public sealed record VerificationResult(bool Passed, string Detail)
{
    public static VerificationResult Ok(string detail) => new(true, detail);

    public static VerificationResult Fail(string detail) => new(false, detail);
}

/// <summary>
/// The integrity source, supplied because the host does not have one.
///
/// fetch.ps1 verifies an artifact against a checksum the vendor publishes beside
/// it. Google Drive publishes no sidecar and, measured on all eleven artifacts,
/// returns no ETag either -- only Last-Modified, which says nothing about
/// content. Rather than drop the check, the missing half is supplied here: an
/// expected length for every artifact, taken from the server's Content-Range,
/// and a SHA-256 wherever a full read has produced one.
///
/// The fetch.ps1 rule is preserved exactly: a mismatch is a hard failure and a
/// file is promoted only after it verifies.
/// </summary>
public static class ArtifactManifest
{
    /// <summary>
    /// Lengths are measured, from the Content-Range header of a four-byte probe
    /// against each id. Hashes are absent wherever the artifact was only ever
    /// sized over the wire; see the class comment for why absent is not a pass.
    /// The two cuBLAS rows are the exception, and the reason is simply that
    /// those files were copied off a local CUDA Toolkit install, so they could
    /// be read in full rather than probed.
    /// </summary>
    public static IReadOnlyList<ArtifactIntegrity> All { get; } =
    [
        // The runtime is a Windows DLL, not a model. Measured 2026-08-02.
        new() { FileId = "12SVa7db8qTjb7daBYGbWulH3EteCeqq1", FileName = "BetterRuntimeCPU.dll", SizeBytes = 3595264 },
        new() { FileId = "12WQ--DVStQb3gH3FcehpnkF7tfTe5eDM", FileName = "BetterRuntimeVulkan.dll", SizeBytes = 54225408 },

        // Measured 2026-08-08, the same way and from the same header: a
        // four-byte Range probe answered 206 with Content-Range total
        // 145155584 and Content-Disposition "BetterRuntimeCUDA.dll". 138.4 MiB,
        // against the 560 MB the catalogue estimated before one was built.
        new() { FileId = "1ipcWlhbPl5JCWI8Lt1Krxt6zclpdxRGp", FileName = "BetterRuntimeCUDA.dll", SizeBytes = 145155584 },

        // The CUDA runtime's own dependencies, not runtimes themselves: it
        // imports cublas64_13.dll, which imports cublasLt64_13.dll, and neither
        // ships with the NVIDIA driver. See B47.
        //
        // The only two rows here carrying a hash. Every other artifact was sized
        // over the wire and never read, but these were copied out of a CUDA
        // Toolkit 13.3 install and hashed off disk, so their contents can be
        // checked and not merely their length.
        new()
        {
            FileId = "10cqIlFhUsJkKQqr1WI_OFOsVZw4R6nKD",
            FileName = "cublas64_13.dll",
            SizeBytes = 52697712,
            Sha256 = "8c6bac24474af29627ec96500025a5d410a1b7e8ffa00e1fe13b1781fc6b3ddc",
        },
        new()
        {
            FileId = "1fyANf9foXLVMHcA5VQON2_YYYFLNSjVJ",
            FileName = "cublasLt64_13.dll",
            SizeBytes = 463655536,
            Sha256 = "e323abd89e0f03c1db91a09d7aeb1928de08e1f86192cc713b335fefe3e368a9",
        },
        new() { FileId = "1qr-fKBqP15oR0rM7XE8N0C6Sr5KJzBu9", FileName = "EuroLLM-9B-Instruct-Q3_K_L.gguf", SizeBytes = 4913846720 },
        new() { FileId = "1WXDoH1U4ermIJI9oTxnzjNyZLzLh4RlK", FileName = "EuroLLM-9B-Instruct-Q4_K_M.gguf", SizeBytes = 5582838208 },
        new() { FileId = "1vNKf7EsgEXUxCAWQAjha0VeVAi_Zsk5e", FileName = "gpt-oss-20b-MXFP4.gguf", SizeBytes = 12109565632 },
        new() { FileId = "15Yt13K9DhtwnHvTHFqh4LYKq6on_KQvr", FileName = "translategemma-4b-it-Q4_K_M.gguf", SizeBytes = 2489909312 },
        new() { FileId = "1CUGS22u5A4C5bt8YHlZc6j1kW9Vi9WzW", FileName = "translategemma-4b-it.mmproj-f16.gguf", SizeBytes = 851251520 },
        new() { FileId = "1LM7u2jN8BLXMPgY8--Z3gz8E-DweEYAe", FileName = "translategemma-4b-it.Q4_K_M.gguf", SizeBytes = 2489893088 },
        new() { FileId = "1_isTnN9CzzQbACa2Tj37aXxmYCwt4rB6", FileName = "translategemma-4b-it.Q4_K_S.gguf", SizeBytes = 2377945600 },
        new() { FileId = "1auwy9dPObLyZ8nS-8WGXRiMPvgPZtiEw", FileName = "translategemma-latest-Q4_K_M.gguf", SizeBytes = 3298866368 },
        new() { FileId = "1cSuGmVRvOrlv7J6BQAOb-ehcylfJ9miH", FileName = "gemma-4-26B-A4B-it-QAT-Q4_0.gguf", SizeBytes = 14439363264 },
        new() { FileId = "1wp9dMol3rKUX2NeES-UHwXvDwHMqAZxo", FileName = "gemma3-1b-Q4_K_M.gguf", SizeBytes = 815310432 },
        new() { FileId = "1WxVNbXVLjPCyGWgr8DuASNnBMtpC-QC6", FileName = "mmproj-gemma-4-26B-A4B-it-QAT-BF16.gguf", SizeBytes = 1194827840 },
    ];

    public static ArtifactIntegrity? For(string fileId) =>
        All.FirstOrDefault(a => a.FileId == fileId);

    public static ArtifactIntegrity? ForFileName(string fileName) =>
        All.FirstOrDefault(a => string.Equals(a.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Checks a completed file. Length first, because it is free and catches the
    /// common failure; then the hash where one is known; then the structural
    /// check, which is what catches an interstitial or an error page that
    /// happened to arrive at the right length.
    /// </summary>
    public static VerificationResult Verify(string path, ArtifactIntegrity expected, string? actualSha256)
    {
        long length;

        try
        {
            length = new FileInfo(path).Length;
        }
        catch (IOException ex)
        {
            return VerificationResult.Fail($"The downloaded file could not be read back: {ex.Message}");
        }

        if (length != expected.SizeBytes)
        {
            return VerificationResult.Fail(
                $"The download is {length:N0} bytes but should be {expected.SizeBytes:N0}. It is incomplete or truncated.");
        }

        // Each kind is held to its own shape. A model must be a GGUF; the
        // runtime is a Windows DLL and must be a PE image. Both exist for the
        // same reason: an error page arriving at the expected length passes the
        // length check, and nothing else would catch it.
        var isModel = expected.FileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);
        var isLibrary = expected.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        uint version = 0;

        if (isModel && !IsGguf(path, out version))
        {
            return VerificationResult.Fail(
                "The download is the right size but is not a GGUF model file. It may be an error page saved to disk.");
        }

        if (isLibrary && !IsWindowsLibrary(path))
        {
            return VerificationResult.Fail(
                "The download is the right size but is not a Windows library. It may be an error page saved to disk.");
        }

        if (expected.Sha256 is null)
        {
            // Named plainly. The caller decides whether to accept it; nothing
            // here reports an unverifiable file as verified.
            return VerificationResult.Ok(
                $"Length{(isModel ? $" and GGUF v{version} header" : isLibrary ? " and PE header" : string.Empty)} checks out. No published checksum exists for this artifact, so its contents are unverified.");
        }

        if (!string.Equals(expected.Sha256, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            return VerificationResult.Fail(
                $"The download does not match its expected checksum. Expected {expected.Sha256}, got {actualSha256 ?? "nothing"}.");
        }

        return VerificationResult.Ok($"Length{(isModel ? $", GGUF v{version} header" : isLibrary ? ", PE header" : string.Empty)} and SHA-256 all match.");
    }

    /// <summary>
    /// Every Windows executable image begins "MZ" -- 4D 5A -- which the shipped
    /// BetterRuntimeCPU.dll was confirmed to do.
    /// </summary>
    public static bool IsWindowsLibrary(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> head = stackalloc byte[2];

            return file.Read(head) == 2 && head[0] == 0x4D && head[1] == 0x5A;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the magic and version only. Proven against all eleven artifacts:
    /// every one begins 47 47 55 46 and declares version 3.
    /// </summary>
    public static bool IsGguf(string path, out uint version)
    {
        version = 0;

        try
        {
            using var file = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];

            if (file.Read(head) != 8 || head[0] != 0x47 || head[1] != 0x47 || head[2] != 0x55 || head[3] != 0x46)
            {
                return false;
            }

            version = BitConverter.ToUInt32(head[4..]);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
