using System.Text;
using System.Text.Json;

namespace BetterTranslator.Engine.Text;

/// <summary>
/// Reads JSON that may have been written on Windows.
///
/// The defect this exists for: a UTF-8 byte-order mark makes both
/// `ConvertFrom-Json` and `JSON.parse` fail at column 1, with an error that
/// reads like a malformed document rather than like an encoding problem --
/// `System.Text.Json` behaves the same way, rejecting 0xEF as an invalid start
/// of a value. `config\languages.json` in the reference engine carries one
/// today, so this is a live case and not a precaution.
///
/// Strip on read. Never write one.
/// </summary>
public static class BomSafeJson
{
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>The bytes without a leading BOM, whether or not one was there.</summary>
    public static ReadOnlySpan<byte> StripBom(ReadOnlySpan<byte> utf8) =>
        utf8.StartsWith(Utf8Bom) ? utf8[Utf8Bom.Length..] : utf8;

    /// <summary>The text without a leading BOM, for callers holding a string.</summary>
    public static string StripBom(string text) =>
        text.Length > 0 && text[0] == '﻿' ? text[1..] : text;

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<T>(StripBom(utf8), options ?? Options);

    public static T? Deserialize<T>(Stream stream, JsonSerializerOptions? options = null)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), options);
    }

    /// <summary>
    /// Case-insensitive because the reference engine's JSON is lower-case while
    /// the C# properties are not, and a mismatch here would read every field as
    /// its default rather than failing loudly.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Writes UTF-8 with no BOM, which is the other half of the rule.</summary>
    public static void WriteAllText(string path, string contents) =>
        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
