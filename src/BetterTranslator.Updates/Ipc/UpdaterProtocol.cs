using System.Buffers;
using System.Text.Json;
using BetterTranslator.Updates.Releases;

namespace BetterTranslator.Updates.Ipc;

public enum UpdaterVerb
{
    Status,
    Check,
    Last,
    Apply,
}

public sealed record UpdaterRequest(UpdaterVerb Verb);

public sealed record UpdaterResponse(bool Ok, string Detail, UpdateStatus? Status)
{
    public static UpdaterResponse Refused(string detail) => new(false, detail, null);
}

public static class UpdaterProtocol
{
    public const string PipeName = "BetterTranslator.Updater";

    public const int MaxRequestBytes = 1024;

    public const int MaxResponseBytes = 8 * 1024;

    private const int MaxVerbLength = 16;

    public static byte[] Serialize(UpdaterRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>(64);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("verb", Name(request.Verb));
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Serialize(UpdaterResponse response)
    {
        var buffer = new ArrayBufferWriter<byte>(512);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", response.Ok);
            writer.WriteString("detail", Clip(response.Detail, 1024));

            if (response.Status is { } status)
            {
                writer.WriteString("outcome", status.Outcome.ToString());
                writer.WriteString("installedVersion", Clip(status.InstalledVersion, 64));
                writer.WriteString("installedCommit", Clip(status.InstalledCommit, 64));
                writer.WriteString("latestVersion", Clip(status.LatestVersion, 64));
                writer.WriteString("latestCommit", Clip(status.LatestCommit, 64));
                writer.WriteBoolean("ready", status.Ready);
                writer.WriteString("checkedUtc", status.CheckedUtc.ToUniversalTime().ToString("O"));
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static bool TryParseRequest(ReadOnlySpan<byte> message, out UpdaterRequest request, out string refusal)
    {
        request = new UpdaterRequest(UpdaterVerb.Status);
        refusal = string.Empty;

        if (message.Length == 0 || message.Length > MaxRequestBytes)
        {
            refusal = "The request is not a size this accepts.";
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(message, new JsonReaderOptions { AllowTrailingCommas = false });
            using var document = JsonDocument.ParseValue(ref reader);

            if (reader.Read())
            {
                refusal = "The request is not readable.";
                return false;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                refusal = "The request is not an object.";
                return false;
            }

            if (!document.RootElement.TryGetProperty("verb", out var verb)
                || verb.ValueKind != JsonValueKind.String)
            {
                refusal = "The request names no verb.";
                return false;
            }

            var name = verb.GetString() ?? string.Empty;

            if (name.Length is 0 or > MaxVerbLength)
            {
                refusal = "The request names no verb.";
                return false;
            }

            if (!TryVerb(name, out var parsed))
            {
                refusal = "That verb is not one this accepts.";
                return false;
            }

            request = new UpdaterRequest(parsed);
            return true;
        }
        catch (JsonException)
        {
            refusal = "The request is not readable.";
            return false;
        }
    }

    public static bool TryParseResponse(ReadOnlySpan<byte> message, out UpdaterResponse response)
    {
        response = UpdaterResponse.Refused("The updater answered with something unreadable.");

        if (message.Length == 0 || message.Length > MaxResponseBytes)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(message, new JsonReaderOptions { AllowTrailingCommas = false });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (reader.Read() || root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var ok = root.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
            var detail = Text(root, "detail");

            if (!root.TryGetProperty("outcome", out var outcomeValue)
                || outcomeValue.ValueKind != JsonValueKind.String
                || !Enum.TryParse<UpdateOutcome>(outcomeValue.GetString(), ignoreCase: false, out var outcome))
            {
                response = new UpdaterResponse(ok, detail, null);
                return true;
            }

            var status = new UpdateStatus(
                outcome,
                detail,
                Text(root, "installedVersion"),
                Text(root, "installedCommit"),
                Text(root, "latestVersion"),
                Text(root, "latestCommit"),
                root.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True,
                DateTimeOffset.TryParse(Text(root, "checkedUtc"), out var moment) ? moment : DateTimeOffset.UtcNow);

            response = new UpdaterResponse(ok, detail, status);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string Name(UpdaterVerb verb) => verb switch
    {
        UpdaterVerb.Check => "check",
        UpdaterVerb.Last => "last",
        UpdaterVerb.Apply => "apply",
        _ => "status",
    };

    private static bool TryVerb(string name, out UpdaterVerb verb)
    {
        switch (name)
        {
            case "status":
                verb = UpdaterVerb.Status;
                return true;
            case "check":
                verb = UpdaterVerb.Check;
                return true;
            case "last":
                verb = UpdaterVerb.Last;
                return true;
            case "apply":
                verb = UpdaterVerb.Apply;
                return true;
            default:
                verb = UpdaterVerb.Status;
                return false;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Clip(value.GetString() ?? string.Empty, 1024)
            : string.Empty;

    private static string Clip(string value, int length) =>
        value.Length <= length ? value : value[..length];
}
