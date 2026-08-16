namespace BetterTranslator.Runtime.Downloads;

/// <summary>
/// What a pasted link turned out to be.
/// </summary>
public sealed record ModelUrlResult
{
    private ModelUrlResult()
    {
    }

    public bool IsAccepted { get; private init; }

    /// <summary>Where the weights will be fetched from.</summary>
    public Uri? DownloadUrl { get; private init; }

    /// <summary>Name shown in the list, derived from the link.</summary>
    public string? ModelName { get; private init; }

    public ModelUrlKind Kind { get; private init; }

    /// <summary>
    /// Why the link was refused, naming the thing that was wrong rather than
    /// saying the link is invalid.
    /// </summary>
    public string? Rejection { get; private init; }

    public static ModelUrlResult Accept(Uri url, string name, ModelUrlKind kind) => new()
    {
        IsAccepted = true,
        DownloadUrl = url,
        ModelName = name,
        Kind = kind,
    };

    public static ModelUrlResult Reject(string reason) => new()
    {
        IsAccepted = false,
        Rejection = reason,
    };
}

public enum ModelUrlKind
{
    HuggingFacePage,
    GgufFile,
    SafetensorsFile,

    /// <summary>A Google Drive file, normalised to its confirmed-download form.</summary>
    GoogleDriveFile,
}

/// <summary>
/// Accepts a Hugging Face model page, a direct .gguf link or a direct
/// .safetensors link. Anything else is refused by name, so pasting a .zip says
/// so rather than failing later in the download.
/// </summary>
public static class ModelUrlParser
{
    private const string HuggingFaceHost = "huggingface.co";
    private const string DriveShareHost = "drive.google.com";
    private const string DriveContentHost = "drive.usercontent.google.com";

    /// <summary>
    /// A Drive id as it appears in a share link: no dots, so it can never be
    /// confused with a bare hostname someone pasted without a scheme.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex BareFileId =
        new("^[A-Za-z0-9_-]{25,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The one form that fetches bytes without OAuth. export=download plus
    /// confirm=t is what skips the virus-scan interstitial on a file too large
    /// to scan; without it the server answers an HTML page with status 200.
    /// </summary>
    public static Uri DriveDownloadUrl(string fileId) =>
        new($"https://{DriveContentHost}/download?id={fileId}&export=download&confirm=t");

    public static ModelUrlResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ModelUrlResult.Reject("Paste a link to a model first.");
        }

        var text = raw.Trim();

        // Before the scheme is guessed: a bare id is not a hostname, and
        // prefixing https:// would turn it into one and mis-reject it.
        if (BareFileId.IsMatch(text) && !text.Contains('.'))
        {
            return AcceptDrive(text);
        }

        // A bare "huggingface.co/owner/model" is what people copy out of the
        // address bar, so accept it without a scheme.
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var url))
        {
            return ModelUrlResult.Reject($"{raw.Trim()} is not a link.");
        }

        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
        {
            return ModelUrlResult.Reject($"{url.Scheme} links are not supported. Use an https link.");
        }

        // Before the extension check: a Drive link carries no extension at all,
        // and would otherwise fall through to "not a model host".
        if (IsDriveHost(url.Host))
        {
            return ParseDrive(url);
        }

        var path = url.AbsolutePath;
        var extension = Path.GetExtension(path);

        if (extension.Equals(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            return ModelUrlResult.Accept(url, Path.GetFileNameWithoutExtension(path), ModelUrlKind.GgufFile);
        }

        if (extension.Equals(".safetensors", StringComparison.OrdinalIgnoreCase))
        {
            return ModelUrlResult.Accept(url, Path.GetFileNameWithoutExtension(path), ModelUrlKind.SafetensorsFile);
        }

        if (url.Host.Equals(HuggingFaceHost, StringComparison.OrdinalIgnoreCase) ||
            url.Host.EndsWith("." + HuggingFaceHost, StringComparison.OrdinalIgnoreCase))
        {
            return ParseHuggingFacePage(url, path);
        }

        // Name the actual thing that was pasted.
        return extension.Length > 0
            ? ModelUrlResult.Reject($"{extension} files are not models. Use a .gguf or .safetensors link, or a Hugging Face page.")
            : ModelUrlResult.Reject($"{url.Host} is not a model host. Use a Hugging Face page, or a direct .gguf or .safetensors link.");
    }

    private static bool IsDriveHost(string host) =>
        host.Equals(DriveShareHost, StringComparison.OrdinalIgnoreCase) ||
        host.Equals(DriveContentHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalises all three shapes people actually paste -- a /file/d/&lt;id&gt;/view
    /// share link, a usercontent download link, and an /open?id= link -- onto the
    /// one confirmed-download URL. A folder link is refused by name, because it
    /// is the mistake most likely to be made and "invalid link" would not tell
    /// anyone what to do about it.
    /// </summary>
    private static ModelUrlResult ParseDrive(Uri url)
    {
        var segments = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length > 0 && segments[0].Equals("drive", StringComparison.OrdinalIgnoreCase))
        {
            return ModelUrlResult.Reject(
                "That is a Google Drive folder, not a file. Open the model file and share that link instead.");
        }

        // /file/d/<id>/view
        if (segments.Length >= 3 &&
            segments[0].Equals("file", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("d", StringComparison.OrdinalIgnoreCase))
        {
            return AcceptDrive(segments[2]);
        }

        // ?id=<id>, which covers both /open and /download. Parsed by hand rather
        // than through HttpUtility: one query parameter is not worth reaching
        // into System.Web for.
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);

            if (split.Length == 2 && split[0].Equals("id", StringComparison.OrdinalIgnoreCase) && split[1].Length > 0)
            {
                return AcceptDrive(Uri.UnescapeDataString(split[1]));
            }
        }

        return ModelUrlResult.Reject(
            "That Google Drive link carries no file id. Use the Share link for the file itself.");
    }

    /// <summary>
    /// The manifest supplies the real filename where the id is a known artifact.
    /// Falling back to the id keeps a pasted unknown id usable, at the cost of a
    /// name that reads like an id until the download reports its own.
    /// </summary>
    private static ModelUrlResult AcceptDrive(string fileId)
    {
        var known = ArtifactManifest.For(fileId);

        return ModelUrlResult.Accept(
            DriveDownloadUrl(fileId),
            known is null ? fileId : Path.GetFileNameWithoutExtension(known.FileName),
            ModelUrlKind.GoogleDriveFile);
    }

    private static ModelUrlResult ParseHuggingFacePage(Uri url, string path)
    {
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // A model page is owner/model. Anything shorter is a user or an index.
        if (segments.Length < 2)
        {
            return ModelUrlResult.Reject("That Hugging Face link is not a model page. Use huggingface.co/owner/model.");
        }

        var owner = segments[0];
        var model = segments[1];

        return ModelUrlResult.Accept(url, $"{owner}/{model}", ModelUrlKind.HuggingFacePage);
    }
}
