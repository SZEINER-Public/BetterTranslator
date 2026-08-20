using System.Security.Cryptography;

namespace BetterTranslator.Updates.Payload;

public sealed record PayloadVerdict(bool Accepted, string Detail, string? Sha256)
{
    public static PayloadVerdict Refused(string detail, string? sha256 = null) => new(false, detail, sha256);
}

public static class PayloadVerifier
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<string> ComputeSha256Async(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<PayloadVerdict> VerifyAsync(
        string file,
        string? expectedSha256,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(file))
        {
            return PayloadVerdict.Refused("The downloaded file is not there.");
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return PayloadVerdict.Refused(
                "This release publishes no SHA-256 for its asset, so the download cannot be verified. "
                + "A release has to carry an asset digest or a checksums file before it can be installed.");
        }

        var actualSize = new FileInfo(file).Length;

        if (expectedSize > 0 && actualSize != expectedSize)
        {
            return PayloadVerdict.Refused(
                $"The download is {actualSize} bytes where the release says {expectedSize}.");
        }

        var actual = await ComputeSha256Async(file, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return PayloadVerdict.Refused(
                $"The download does not match its published checksum. Expected {expectedSha256.Trim()}, got {actual}.",
                actual);
        }

        var signature = Authenticode.Verify(file);

        if (signature.State == SignatureState.Invalid)
        {
            return PayloadVerdict.Refused(signature.Detail, actual);
        }

        return new PayloadVerdict(true, "The download matches its published checksum. " + signature.Detail, actual);
    }

    public static string? FindChecksum(string checksumsFile, string assetName)
    {
        foreach (var line in checksumsFile.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)
            {
                continue;
            }

            var hash = parts[0].Trim();
            var name = parts[^1].TrimStart('*', '.', '/', '\\');

            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(name), assetName, StringComparison.OrdinalIgnoreCase))
            {
                return hash.ToLowerInvariant();
            }
        }

        return null;
    }
}
