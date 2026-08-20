using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace BetterTranslator.Updates.Ipc;

public enum AppInstanceVerb
{
    Ping,
    Notify,
    Activate,
    Close,
}

public sealed record AppInstanceRequest(AppInstanceVerb Verb, string Arguments);

public sealed record AppInstanceReply(bool Ok, string Detail)
{
    public static AppInstanceReply Refused(string detail) => new(false, detail);

    public static AppInstanceReply Done(string detail) => new(true, detail);
}

public static class AppInstanceProtocol
{
    public const int MaxRequestBytes = 1024;

    public const int MaxReplyBytes = 2048;

    public const int MaxArgumentsLength = 256;

    private const int MaxVerbLength = 16;

    public static string PipeName() => PipeName(Process.GetCurrentProcess().SessionId);

    public static string PipeName(int sessionId) =>
        "BetterTranslator.App." + sessionId.ToString(CultureInfo.InvariantCulture);

    public static byte[] Serialize(AppInstanceRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>(128);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("verb", Name(request.Verb));
            writer.WriteString("arguments", request.Arguments);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Serialize(AppInstanceReply reply)
    {
        var buffer = new ArrayBufferWriter<byte>(128);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", reply.Ok);
            writer.WriteString("detail", reply.Detail.Length <= 512 ? reply.Detail : reply.Detail[..512]);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static bool TryParseRequest(ReadOnlySpan<byte> message, out AppInstanceRequest request, out string refusal)
    {
        request = new AppInstanceRequest(AppInstanceVerb.Ping, string.Empty);
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

            if (reader.Read() || document.RootElement.ValueKind != JsonValueKind.Object)
            {
                refusal = "The request is not readable.";
                return false;
            }

            if (!document.RootElement.TryGetProperty("verb", out var verb)
                || verb.ValueKind != JsonValueKind.String
                || verb.GetString() is not { Length: > 0 and <= MaxVerbLength } name
                || !TryVerb(name, out var parsed))
            {
                refusal = "That verb is not one this accepts.";
                return false;
            }

            var arguments = document.RootElement.TryGetProperty("arguments", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;

            if (arguments.Length > MaxArgumentsLength)
            {
                refusal = "The request carries more than this accepts.";
                return false;
            }

            request = new AppInstanceRequest(parsed, arguments);
            return true;
        }
        catch (JsonException)
        {
            refusal = "The request is not readable.";
            return false;
        }
    }

    public static bool TryParseReply(ReadOnlySpan<byte> message, out AppInstanceReply reply)
    {
        reply = AppInstanceReply.Refused("The application answered with something unreadable.");

        if (message.Length == 0 || message.Length > MaxReplyBytes)
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

            var detail = root.TryGetProperty("detail", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

            reply = new AppInstanceReply(
                root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                detail.Length <= 512 ? detail : detail[..512]);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string Name(AppInstanceVerb verb) => verb switch
    {
        AppInstanceVerb.Notify => "notify",
        AppInstanceVerb.Activate => "activate",
        AppInstanceVerb.Close => "close",
        _ => "ping",
    };

    private static bool TryVerb(string name, out AppInstanceVerb verb)
    {
        switch (name)
        {
            case "ping":
                verb = AppInstanceVerb.Ping;
                return true;
            case "notify":
                verb = AppInstanceVerb.Notify;
                return true;
            case "activate":
                verb = AppInstanceVerb.Activate;
                return true;
            case "close":
                verb = AppInstanceVerb.Close;
                return true;
            default:
                verb = AppInstanceVerb.Ping;
                return false;
        }
    }
}
