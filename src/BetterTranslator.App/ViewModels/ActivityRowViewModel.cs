using System.Windows.Media;
using BetterTranslator.App.Services;
using BetterTranslator.Indexing.Retrieval;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// C21 LogRow. A coloured dot, a primary line and a monospace subtitle naming
/// what happened.
/// </summary>
public sealed record ActivityRowViewModel(string Line, string Detail, Brush Dot)
{
    public static ActivityRowViewModel From(RetrievalEvent activity) => new(
        activity.Line,
        activity.Detail,
        activity.Kind switch
        {
            RetrievalKind.Dense => Tokens.Get<Brush>("BrushAccentDeep"),
            RetrievalKind.Lexical => Tokens.Get<Brush>("BrushRepoSource"),
            _ => Tokens.Get<Brush>("BrushSuccess"),
        });

    /// <summary>An indexing step, which is amber while it runs.</summary>
    public static ActivityRowViewModel Indexing(string line, string detail) =>
        new(line, detail, Tokens.Get<Brush>("BrushWarn"));

    /// <summary>A finished step.</summary>
    public static ActivityRowViewModel Done(string line, string detail) =>
        new(line, detail, Tokens.Get<Brush>("BrushSuccess"));
}
