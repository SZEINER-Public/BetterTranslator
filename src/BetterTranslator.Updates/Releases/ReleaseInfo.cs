namespace BetterTranslator.Updates.Releases;

public sealed record ReleaseAsset(string Name, long SizeBytes, string DownloadUrl, string? Sha256);

public sealed record ReleaseInfo(
    string Tag,
    string Commit,
    DateTimeOffset PublishedUtc,
    IReadOnlyList<ReleaseAsset> Assets)
{
    public const int MaxNotesLength = 400;

    public const string ChecksumAssetName = "checksums.txt";

    public string Notes { get; init; } = string.Empty;

    public string PageUrl { get; init; } = string.Empty;

    public bool HasCommit => Commit.Length > 0;

    public ReleaseAsset? Payload(string name) =>
        Assets.FirstOrDefault(asset => string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase));

    public ReleaseAsset? Checksums() =>
        Assets.FirstOrDefault(asset => string.Equals(asset.Name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
        ?? Assets.FirstOrDefault(asset => asset.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase));
}

public sealed record ReleaseLookup(bool Ok, ReleaseInfo? Release, string Detail)
{
    public static ReleaseLookup Found(ReleaseInfo release) => new(true, release, string.Empty);

    public static ReleaseLookup Failed(string detail) => new(false, null, detail);
}

public interface IReleaseClient
{
    Task<ReleaseLookup> LatestAsync(CancellationToken cancellationToken);
}
