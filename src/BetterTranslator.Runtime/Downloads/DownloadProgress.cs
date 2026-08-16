namespace BetterTranslator.Runtime.Downloads;

public enum DownloadState
{
    Waiting,
    Downloading,
    Installed,
    Failed,
    Cancelled,
}

/// <summary>
/// A snapshot of one component's transfer. Everything a progress row shows
/// comes from here, so the row never computes a figure of its own.
/// </summary>
public sealed record DownloadProgress
{
    public required string ComponentId { get; init; }

    public required DownloadState State { get; init; }

    public long BytesSoFar { get; init; }

    public long TotalBytes { get; init; }

    public double BytesPerSecond { get; init; }

    public TimeSpan? Remaining { get; init; }

    /// <summary>Whole percent, floored, so a row never reads 100 before it is done.</summary>
    public int Percent => TotalBytes <= 0
        ? 0
        : (int)Math.Floor(Math.Clamp((double)BytesSoFar / TotalBytes, 0, 1) * 100);

    /// <summary>Why the transfer failed, in plain language. Null otherwise.</summary>
    public string? Failure { get; init; }

    /// <summary>
    /// What happened, when that is worth saying: which check passed, or that the
    /// file was already on the machine so nothing was fetched. A row that says
    /// "installed" without saying it downloaded nothing invites the reader to
    /// wait for a transfer that is not running.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// SHA-256 of what was written, where a full download computed one. Null on
    /// a resumed transfer, which cannot hash bytes it never read.
    /// </summary>
    public string? Sha256 { get; init; }
}
