using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using BetterTranslator.Updates.Notifications;
using BetterTranslator.Updates.Releases;

namespace BetterTranslator.App.Notifications;

public static class ToastContent
{
    public const string Tag = "update";

    public const string Group = "BetterTranslator.Update";

    private const int MaxNotesLength = 180;

    public static string Build(PendingUpdateNotice notice, string installedVersion, string? logoFile)
    {
        var payload = new StringBuilder();

        using (var writer = XmlWriter.Create(
                   new StringWriter(payload, CultureInfo.InvariantCulture),
                   new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false }))
        {
            writer.WriteStartElement("toast");
            writer.WriteAttributeString("launch", ToastActivationPayload.Build(ToastAction.Open, notice.Version, notice.Commit));
            writer.WriteAttributeString("activationType", "foreground");

            writer.WriteStartElement("visual");
            writer.WriteStartElement("binding");
            writer.WriteAttributeString("template", "ToastGeneric");

            writer.WriteStartElement("text");
            writer.WriteString("Update is available");
            writer.WriteEndElement();

            writer.WriteStartElement("text");
            writer.WriteString(Summary(notice, installedVersion));
            writer.WriteEndElement();

            if (logoFile is { Length: > 0 } && File.Exists(logoFile))
            {
                writer.WriteStartElement("image");
                writer.WriteAttributeString("placement", "appLogoOverride");
                writer.WriteAttributeString("src", new Uri(logoFile).AbsoluteUri);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("actions");

            WriteAction(writer, "Install now", ToastActivationPayload.Build(ToastAction.Install, notice.Version, notice.Commit));
            WriteAction(writer, "Later", ToastActivationPayload.Build(ToastAction.Later, notice.Version, notice.Commit));

            if (GitHubReleaseClient.IsReleasePage(notice.PageUrl))
            {
                writer.WriteStartElement("action");
                writer.WriteAttributeString("content", "Release notes");
                writer.WriteAttributeString("activationType", "protocol");
                writer.WriteAttributeString("arguments", notice.PageUrl);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return payload.ToString();
    }

    private static void WriteAction(XmlWriter writer, string content, string arguments)
    {
        writer.WriteStartElement("action");
        writer.WriteAttributeString("content", content);
        writer.WriteAttributeString("activationType", "foreground");
        writer.WriteAttributeString("arguments", arguments);
        writer.WriteEndElement();
    }

    /// <summary>
    /// The two version numbers and nothing else. What changed belongs in the
    /// release notes the notification already links to, not in a toast.
    /// </summary>
    private static string Summary(PendingUpdateNotice notice, string installedVersion)
    {
        var current = installedVersion.Length > 0 ? installedVersion : notice.InstalledVersion;

        return current.Length > 0 ? $"{current} → {notice.Version}" : notice.Version;
    }

    public static string Notes(string body)
    {
        var cleaned = new StringBuilder(Math.Min(body.Length, MaxNotesLength));

        foreach (var character in body)
        {
            if (cleaned.Length >= MaxNotesLength)
            {
                break;
            }

            if (char.IsControl(character))
            {
                if (cleaned.Length > 0 && cleaned[^1] != ' ')
                {
                    cleaned.Append(' ');
                }

                continue;
            }

            cleaned.Append(character);
        }

        return cleaned.ToString().Trim();
    }
}
