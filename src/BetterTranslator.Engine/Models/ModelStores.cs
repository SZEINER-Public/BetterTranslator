using System.Text.Json;
using BetterTranslator.Engine.Text;

namespace BetterTranslator.Engine.Models;

/// <summary>One place models might already be, and why it is a candidate.</summary>
public sealed record ModelStore(string Path, string Rule);

/// <summary>
/// Where models already are on this machine. Ported from `store-resolve.ps1`.
///
/// The point is not tidiness. A user who already has models sitting in the store
/// another tool populated should not download them a second time, and on the
/// machine this was built against that was 33 GiB of 47. Guessing one fixed
/// folder gets it wrong for anyone who moved theirs.
/// </summary>
public static class ModelStores
{
    /// <summary>
    /// The user's real profile.
    ///
    /// <c>%USERPROFILE%</c> alone is not enough: it is a plain environment
    /// variable and is wrong or empty under a service account, a scheduled task
    /// and some elevation paths, which is exactly where a store lookup silently
    /// resolves to nothing.
    /// </summary>
    public static string UserProfile()
    {
        try
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (folder.Length > 0 && Directory.Exists(folder))
            {
                return folder;
            }
        }
        catch (PlatformNotSupportedException)
        {
        }

        var joined = Environment.GetEnvironmentVariable("HOMEDRIVE") + Environment.GetEnvironmentVariable("HOMEPATH");

        return joined.Length > 0 && Directory.Exists(joined)
            ? joined
            : Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
    }

    /// <summary>
    /// The shared store, which is the conventional location and the one this app
    /// treats as the default place to look.
    /// </summary>
    public static string SharedDefault() => Path.Combine(UserProfile(), ".lmstudio-shared", "models");

    /// <summary>
    /// LM Studio's own downloads folder, read from its settings rather than
    /// assumed. Someone who moved their models to another drive -- which is the
    /// usual reason to have fifteen-gigabyte files at all -- would otherwise have
    /// every one of them invisible here.
    /// </summary>
    public static string? LmStudioUserStore()
    {
        var profile = UserProfile();
        var settings = Path.Combine(profile, ".lmstudio", "settings.json");

        if (File.Exists(settings))
        {
            try
            {
                using var document = JsonDocument.Parse(BomSafeJson.StripBom(File.ReadAllText(settings)));

                if (document.RootElement.TryGetProperty("downloadsFolder", out var folder)
                    && folder.ValueKind == JsonValueKind.String
                    && folder.GetString() is { Length: > 0 } path)
                {
                    return path;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // An unreadable settings file is no pointer, not a failure: the
                // fallback below is what LM Studio itself uses.
            }
        }

        var fallback = Path.Combine(profile, ".lmstudio", "models");

        return Directory.Exists(fallback) ? fallback : null;
    }

    /// <summary>
    /// Every place worth looking, best first, with duplicates and empties gone.
    /// </summary>
    /// <param name="preferred">
    /// The folder the app installs into, which always leads: what the user chose
    /// here outranks what another tool decided elsewhere.
    /// </param>
    public static IReadOnlyList<ModelStore> Candidates(string? preferred = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stores = new List<ModelStore>();

        void Add(string? path, string rule)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string full;

            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (seen.Add(full))
            {
                stores.Add(new ModelStore(full, rule));
            }
        }

        Add(preferred, "chosen");
        Add(Environment.GetEnvironmentVariable("BETTERTRANSLATOR_MODEL_STORE"), "environment");
        Add(LmStudioUserStore(), "lm-studio");
        Add(SharedDefault(), "shared-default");

        return stores;
    }

    /// <summary>
    /// Can this store actually be written to?
    ///
    /// Asked by writing a probe file, not by inspecting an ACL: a folder can look
    /// permitted and still be refused by a redirected profile, a full disk or a
    /// read-only mount, and finding that out at byte one of a fifteen-gigabyte
    /// download is far worse than finding it out here.
    /// </summary>
    /// <param name="mayCreate">
    /// False for a read-only check, which must create nothing but still has to
    /// report the store it WOULD use -- so a not-yet-existing folder is judged by
    /// its nearest existing ancestor. Returning false there instead made the
    /// reference's own check claim no store could be resolved at all.
    /// </param>
    public static bool IsWritable(string path, bool mayCreate = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var target = path;

        try
        {
            if (!Directory.Exists(target))
            {
                if (mayCreate)
                {
                    Directory.CreateDirectory(target);
                }
                else
                {
                    target = NearestExistingAncestor(target) ?? string.Empty;

                    if (target.Length == 0)
                    {
                        return false;
                    }
                }
            }

            var probe = Path.Combine(target, ".write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");

            File.WriteAllText(probe, "probe");
            File.Delete(probe);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? NearestExistingAncestor(string path)
    {
        var probe = path;

        while (probe.Length > 0 && !Directory.Exists(probe))
        {
            var parent = Path.GetDirectoryName(probe);

            if (string.IsNullOrEmpty(parent) || parent == probe)
            {
                return null;
            }

            probe = parent;
        }

        return probe.Length > 0 && Directory.Exists(probe) ? probe : null;
    }
}
