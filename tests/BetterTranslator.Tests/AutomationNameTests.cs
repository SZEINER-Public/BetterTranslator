using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What assistive technology is told a control is.
///
/// A name is not a label: WPF derives one from string content and from nothing
/// else, so a container bound to a view model announces that type's name, and a
/// button whose content is a panel announces nothing at all. Both shipped. The
/// peers are asked here rather than the markup, because a binding that resolves
/// to an empty string is the same defect as a binding that was never written.
/// </summary>
public sealed class AutomationNameTests
{
    public sealed class Row
    {
        public string Name { get; init; } = string.Empty;
        public string Stamp { get; init; } = string.Empty;
        public int NameLength { get; init; }
        public bool IsNaming { get; init; }
        public string Tooltip { get; init; } = string.Empty;
    }

    public sealed class Dialog
    {
        public string Title { get; init; } = string.Empty;
        public string Body { get; init; } = string.Empty;
        public string ConfirmLabel { get; init; } = string.Empty;
        public string CancelLabel { get; init; } = string.Empty;
        public string ConfirmIconKey { get; init; } = string.Empty;
        public string CancelIconKey { get; init; } = string.Empty;
    }

    [Fact]
    public void A_chat_row_announces_the_chat_it_holds()
    {
        var failure = StaRunner.Run(() => WithTokens(() =>
        {
            var list = (Style)Application.Current!.Resources["ChatRowList"];

            var container = list.Setters
                .OfType<Setter>()
                .Where(setter => setter.Property == ItemsControl.ItemContainerStyleProperty)
                .Select(setter => setter.Value as Style)
                .SingleOrDefault();

            container.Should().NotBeNull("the list declares how its rows are built");

            // Context first, then the style, then a measure. A style setter
            // bound to the data resolves when the style is applied, and applying
            // one to a row that does not yet know what it holds resolves it to
            // nothing.
            var row = new ListBoxItem
            {
                DataContext = new Row { Name = "Soubor README.md", Stamp = "10 min ago", NameLength = 16 },
            };

            row.Style = container;
            row.Measure(new Size(240, 48));

            var peer = UIElementAutomationPeer.CreatePeerForElement(row);

            peer.Should().NotBeNull();
            peer!.GetName().Should().Be("Soubor README.md");
            peer.GetName().Should().NotContain("ViewModel", "the type name is not a chat");

            return null;
        }));

        failure.Should().BeNull();
    }

    [Fact]
    public void Both_buttons_on_a_confirmation_announce_what_they_do()
    {
        var failure = StaRunner.Run(() => WithTokens(() =>
        {
            var window = (Window)Application.LoadComponent(
                new Uri("/BetterTranslator;component/MainWindow.xaml", UriKind.Relative));

            // The window hosts several dialog surfaces. The confirmation is the
            // one the modal layer binds to whichever dialog is active.
            var surface = Descendants<Border>(window)
                .Single(border => System.Windows.Data.BindingOperations
                    .GetBinding(border, FrameworkElement.DataContextProperty)?.Path.Path == "ActiveDialog");

            // The window's own markup, with the view model swapped for a stand
            // in: these are the buttons the prompt puts in front of a reader.
            surface.DataContext = new Dialog
            {
                Title = "Delete this chat?",
                Body = "Soubor README.md and its translations are removed.",
                ConfirmLabel = "Delete",
                CancelLabel = "Cancel",
            };

            // A binding on an element no layout has touched has not run yet, so
            // reading the names before this measures nothing but the default.
            surface.Measure(new Size(500, 400));
            surface.UpdateLayout();

            var names = Descendants<Button>(surface)
                .Select(button => UIElementAutomationPeer.CreatePeerForElement(button)?.GetName() ?? string.Empty)
                .ToList();

            names.Should().HaveCount(2);
            names.Should().BeEquivalentTo(new[] { "Cancel", "Delete" });
            names.Should().NotContain(
                string.Empty,
                "an unnamed button on a prompt where one choice destroys a chat is a coin toss");

            return null;
        }));

        failure.Should().BeNull();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Restored on the way out. Application.Current is process wide, and leaving
    /// these merged changes what unrelated tests see when they ask the token
    /// store a question.
    /// </summary>
    private static string? WithTokens(Func<string?> body)
    {
        var added = new List<ResourceDictionary>();

        foreach (var name in Dictionaries)
        {
            var source = new Uri(
                $"pack://application:,,,/BetterTranslator;component/{name}.xaml",
                UriKind.Absolute);

            if (Application.Current!.Resources.MergedDictionaries.All(d => d.Source != source))
            {
                var dictionary = new ResourceDictionary { Source = source };

                Application.Current.Resources.MergedDictionaries.Add(dictionary);
                added.Add(dictionary);
            }
        }

        try
        {
            return body();
        }
        catch (Exception ex)
        {
            return ex.ToString();
        }
        finally
        {
            foreach (var dictionary in added)
            {
                Application.Current!.Resources.MergedDictionaries.Remove(dictionary);
            }
        }
    }

    private static readonly string[] Dictionaries =
    [
        "Themes/Colors", "Themes/Typography", "Themes/Metrics", "Themes/Icons",
        "Themes/Flags", "Themes/Controls", "Themes/Markdown", "Themes/Json",
        "Verification/VerificationTokens",
    ];
}
