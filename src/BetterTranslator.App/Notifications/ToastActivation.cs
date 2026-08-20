using System.Text;

namespace BetterTranslator.App.Notifications;

public enum ToastAction
{
    Open,
    Install,
    Later,
}

public sealed record ToastActivation(ToastAction Action, string Version, string Commit)
{
    public string Identity => Updates.Notifications.ReleaseIdentity.Of(Version, Commit);
}

public static class ToastActivationPayload
{
    public const int MaxLength = 256;

    private const int MaxVersionLength = 32;
    private const int MaxCommitLength = 40;

    public static string Build(ToastAction action, string version, string commit)
    {
        var text = new StringBuilder();

        text.Append("action=").Append(Name(action));

        if (IsVersion(version))
        {
            text.Append("&version=").Append(version);
        }

        if (IsCommit(commit))
        {
            text.Append("&commit=").Append(commit);
        }

        return text.ToString();
    }

    public static bool TryParse(string? payload, out ToastActivation activation)
    {
        activation = new ToastActivation(ToastAction.Open, string.Empty, string.Empty);

        if (string.IsNullOrEmpty(payload) || payload.Length > MaxLength)
        {
            return false;
        }

        var action = ToastAction.Open;
        var version = string.Empty;
        var commit = string.Empty;
        var seen = 0;

        foreach (var pair in payload.Split('&'))
        {
            if (pair.Length == 0 || ++seen > 8)
            {
                return false;
            }

            var split = pair.IndexOf('=');

            if (split <= 0 || split == pair.Length - 1)
            {
                return false;
            }

            var key = pair[..split];
            var value = pair[(split + 1)..];

            switch (key)
            {
                case "action" when TryAction(value, out var parsed):
                    action = parsed;
                    break;
                case "version" when IsVersion(value):
                    version = value;
                    break;
                case "commit" when IsCommit(value):
                    commit = value;
                    break;
                default:
                    return false;
            }
        }

        activation = new ToastActivation(action, version, commit);

        return true;
    }

    private static bool TryAction(string value, out ToastAction action)
    {
        switch (value)
        {
            case "open":
                action = ToastAction.Open;
                return true;
            case "install":
                action = ToastAction.Install;
                return true;
            case "later":
                action = ToastAction.Later;
                return true;
            default:
                action = ToastAction.Open;
                return false;
        }
    }

    public static string Name(ToastAction action) => action switch
    {
        ToastAction.Install => "install",
        ToastAction.Later => "later",
        _ => "open",
    };

    private static bool IsVersion(string value) =>
        value.Length is > 0 and <= MaxVersionLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');

    private static bool IsCommit(string value) =>
        value.Length is > 0 and <= MaxCommitLength && value.All(Uri.IsHexDigit);
}
