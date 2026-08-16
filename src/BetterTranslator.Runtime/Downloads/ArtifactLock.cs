using System.Security.Cryptography;
using System.Text;

namespace BetterTranslator.Runtime.Downloads;

/// <summary>
/// Serialises downloads of one artifact across the whole machine.
///
/// The models folder is shared -- with a second instance of this app, and with
/// whatever else populates the LM Studio store -- so two writers can arrive at
/// one filename. Without this they interleave into the same .part and produce a
/// file that is the right length and complete nonsense. This is store-lock.ps1's
/// job, kept cross-process for the same reason: an in-process semaphore would
/// only protect one of the two instances that can collide.
///
/// Held as an exclusive file rather than a named Mutex. A Mutex is owned by a
/// thread, and this code awaits: the continuation after an await can resume on a
/// different thread-pool thread, which makes ReleaseMutex throw, and can resume
/// on the *same* thread as another download, which lets that one walk straight
/// through a lock it does not own. A file handle is owned by the process and has
/// neither problem.
/// </summary>
public sealed class ArtifactLock : IDisposable
{
    private readonly FileStream? _handle;
    private readonly string? _path;

    private ArtifactLock(FileStream? handle, string? path)
    {
        _handle = handle;
        _path = path;
    }

    /// <summary>
    /// Waits for whoever is fetching this artifact to finish. Returns a lock
    /// holding nothing if one cannot be taken at all, because a download that
    /// proceeds unserialised beats one that never starts -- and the caller
    /// re-checks presence after the wait, which is what makes that safe.
    /// </summary>
    public static async Task<ArtifactLock> AcquireAsync(string fileName, CancellationToken cancellationToken)
    {
        // Beside the temp folder rather than in the models folder: a lock file
        // among the models would be picked up by a folder scan, and cleaning it
        // up is not the scanner's job.
        var directory = Path.Combine(Path.GetTempPath(), "BetterTranslator.locks");
        var path = Path.Combine(directory, Fingerprint(fileName) + ".lock");

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (IOException)
        {
            return new ArtifactLock(null, null);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // DeleteOnClose so a process that dies mid-download does not
                // leave a lock nobody can clear.
                var handle = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);

                return new ArtifactLock(handle, path);
            }
            catch (IOException)
            {
                // Someone else holds it. Polled rather than blocked so a cancel
                // the UI has already shown can still be answered while queueing.
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                return new ArtifactLock(null, null);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ArtifactLock(null, null);
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..32];

    public void Dispose()
    {
        try
        {
            _handle?.Dispose();
        }
        catch (IOException)
        {
            // Already gone; nothing to release.
        }
    }
}
