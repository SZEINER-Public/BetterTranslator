using System.Security.Cryptography;
using System.Text;

namespace BetterTranslator.Indexing;

/// <summary>
/// The content hash stored with a source and compared against on a reindex.
///
/// One implementation on purpose: the indexer writes it and the reindex
/// resolver reads it, and if the two ever computed it differently every source
/// would read as changed forever.
/// </summary>
public static class ContentHash
{
    public static string Of(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>
    /// A file's hash over its bytes rather than its text, so the same content
    /// under two names is recognised as one thing.
    ///
    /// Full length, unlike <see cref="Of"/>: this decides whether an attachment
    /// is a duplicate of one already there, and a truncated hash would trade
    /// certainty for nothing. Reads the file, so it takes a token and is never
    /// called on the UI thread.
    /// </summary>
    public static async Task<string> OfFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);

        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
