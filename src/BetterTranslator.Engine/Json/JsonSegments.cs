namespace BetterTranslator.Engine.Json;

/// <summary>What kind of scalar sits at a leaf.</summary>
public enum JsonScalarKind
{
    String,
    Number,
    True,
    False,
    Null,
}

/// <summary>
/// One scalar in the document, and exactly where its raw text sits in it.
/// </summary>
/// <param name="KeyPath">
/// How the value is reached, for example `app.actions.save` or `items.[2].label`.
/// Carried for the reader's benefit and for mapping a batched answer back to a
/// place that can be named in a log; the splice itself goes by span.
/// </param>
/// <param name="Start">
/// Byte offset of the raw text in the UTF-8 document -- inside the quotes for a
/// string, at the first character for anything else. Bytes rather than
/// characters because the splice is done on bytes: that is what makes "identical
/// except inside string values" a fact about the output rather than a hope.
/// </param>
/// <param name="Text">
/// The decoded value for a string, so `He said \"hi\"` arrives as `He said "hi"`.
/// The literal as written for everything else.
/// </param>
public sealed record JsonScalar(
    string KeyPath,
    int Depth,
    JsonScalarKind Kind,
    int Start,
    int Length,
    string Text)
{
    /// <summary>
    /// A key is never translated, so only values reach here at all. Of those,
    /// only strings carry language: a number, a boolean and a null say the same
    /// thing in every locale.
    /// </summary>
    public bool IsString => Kind == JsonScalarKind.String;
}

/// <summary>One value lifted out of a translated string, with its sentinel.</summary>
public sealed record JsonGuard(string Sentinel, string Original);

/// <summary>How a document came out.</summary>
/// <param name="Text">
/// The translated document, or the source unchanged when nothing usable came
/// back for anything in it.
/// </param>
/// <param name="Translated">Values the model answered and that validated.</param>
/// <param name="Kept">
/// Values nothing usable came back for. They keep their source text; the
/// document is still valid JSON either way.
/// </param>
public sealed record JsonTranslationResult(
    string Text,
    int Translated,
    int Kept,
    IReadOnlyList<string> KeptPaths);
