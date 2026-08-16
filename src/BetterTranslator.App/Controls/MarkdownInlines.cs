using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using BetterTranslator.App.Services;
using BetterTranslator.Indexing.Markdown;

namespace BetterTranslator.App.Controls;

/// <summary>
/// The one place a run of Markdown inlines becomes WPF inlines.
///
/// Shared rather than duplicated: the same document is drawn into a TextBlock by
/// <see cref="MarkdownText"/> and into a FlowDocument by
/// <see cref="MarkdownFlow"/>, and a second copy of the mark rules would drift
/// from the first the moment either was touched.
/// </summary>
internal static class MarkdownInlines
{
    public static void Fill(InlineCollection into, IEnumerable<MdInline>? inlines)
    {
        into.Clear();

        if (inlines is null)
        {
            return;
        }

        foreach (var inline in inlines)
        {
            into.Add(Build(inline));
        }
    }

    public static Inline Build(MdInline inline)
    {
        var run = new Run(inline.Text);

        if (inline.Bold)
        {
            run.FontWeight = FontWeights.SemiBold;
        }

        if (inline.Italic)
        {
            run.FontStyle = FontStyles.Italic;
        }

        if (inline.Code)
        {
            run.FontFamily = Tokens.Get<FontFamily>("FontFamilyMono");
            run.Background = Tokens.Get<Brush>("BrushSurfaceSunken");
        }

        return inline.Link is null ? run : Anchor(run, inline.Link);
    }

    /// <summary>
    /// A link, opened in whatever the machine uses for the web rather than in
    /// the application. The target is shown as the tooltip, because a link whose
    /// text was translated says nothing about where it goes.
    /// </summary>
    private static Hyperlink Anchor(Run run, string url)
    {
        var link = new Hyperlink(run)
        {
            Foreground = Tokens.Get<Brush>("BrushAccent"),
            ToolTip = url,
            TextDecorations = TextDecorations.Underline,
        };

        if (Uri.TryCreate(url, UriKind.Absolute, out var target)
            && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps))
        {
            // Only http and https are followed. A relative target has nothing to
            // resolve against in a chat message, and every other scheme is a way
            // to hand a pasted document a launcher.
            link.NavigateUri = target;
            link.RequestNavigate += OnRequestNavigate;
        }

        return link;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser registered, or the shell refused. A link that cannot be
            // opened does nothing rather than taking the window down with it.
        }
    }
}
