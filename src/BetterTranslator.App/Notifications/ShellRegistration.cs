using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BetterTranslator.App.Notifications;

public interface IRegistryStore
{
    string? Read(string key);

    void Write(string key, string value);

    void Remove(string key);
}

public interface IShortcutStore
{
    string? TargetOf(string path);

    bool Create(string path, string target, string appUserModelId, Guid activator);

    void Remove(string path);
}

public sealed record RegistrationOutcome(bool Registered, bool Changed, string Detail);

public sealed class ShellRegistration(
    IRegistryStore registry,
    IShortcutStore shortcuts,
    string executable,
    string? shortcutPath = null)
{
    private readonly string _shortcut = shortcutPath ?? ShellIdentity.ShortcutPath;

    public string ShortcutPath => _shortcut;

    public bool IsRegistered =>
        string.Equals(shortcuts.TargetOf(_shortcut), executable, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            registry.Read(ShellIdentity.LocalServerKey),
            ShellIdentity.LocalServerCommand(executable),
            StringComparison.OrdinalIgnoreCase);

    public RegistrationOutcome Register()
    {
        if (executable.Length == 0)
        {
            return new RegistrationOutcome(false, false, "This build has no path to register.");
        }

        var changed = false;

        var command = ShellIdentity.LocalServerCommand(executable);

        if (!string.Equals(registry.Read(ShellIdentity.ClsidKey), "BetterTranslator", StringComparison.Ordinal))
        {
            registry.Write(ShellIdentity.ClsidKey, "BetterTranslator");
            changed = true;
        }

        if (!string.Equals(registry.Read(ShellIdentity.LocalServerKey), command, StringComparison.OrdinalIgnoreCase))
        {
            registry.Write(ShellIdentity.LocalServerKey, command);
            changed = true;
        }

        if (!string.Equals(shortcuts.TargetOf(_shortcut), executable, StringComparison.OrdinalIgnoreCase))
        {
            if (!shortcuts.Create(_shortcut, executable, ShellIdentity.AppUserModelId, ShellIdentity.Activator))
            {
                return new RegistrationOutcome(false, changed, "The Start Menu shortcut could not be written.");
            }

            changed = true;
        }

        return new RegistrationOutcome(true, changed, changed ? "Registered for notifications." : "Already registered.");
    }

    public void Remove()
    {
        shortcuts.Remove(_shortcut);
        registry.Remove(ShellIdentity.ClsidKey);
    }
}

[SupportedOSPlatform("windows")]
public sealed class UserRegistryStore : IRegistryStore
{
    private const string Allowed = @"Software\Classes\CLSID\";

    public string? Read(string key)
    {
        Guard(key);

        try
        {
            using var opened = Registry.CurrentUser.OpenSubKey(key);

            return opened?.GetValue(null) as string;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string key, string value)
    {
        Guard(key);

        using var created = Registry.CurrentUser.CreateSubKey(key, writable: true);

        created?.SetValue(null, value, RegistryValueKind.String);
    }

    public void Remove(string key)
    {
        Guard(key);

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
        catch (System.Security.SecurityException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Guard(string key)
    {
        if (!key.StartsWith(Allowed, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only the per-user activator key is written.", nameof(key));
        }
    }
}
