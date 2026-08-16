using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// The conversation list virtualizes, asserted rather than read.
///
/// It did not, and the reason was invisible in the style: `ChatRowList` asked
/// for virtualization and got none, because the list sat inside a stack panel
/// inside another scroll viewer and both hand a child infinite height. A panel
/// measured that way realizes every row whatever its settings say. So there are
/// two assertions here, and the second is the one that would have caught it:
/// the settings, and the place the list is put.
/// </summary>
public sealed class SidebarVirtualizationTests(ITestOutputHelper output)
{
    [Fact]
    public void TheChatRowListAsksForVirtualizationWithRecycling()
    {
        var style = (Style)Dictionary("Themes/Controls")["ChatRowList"];
        var setters = style.Setters.OfType<Setter>().ToDictionary(s => s.Property.Name, s => s.Value);

        output.WriteLine(string.Join(", ", setters.Select(s => $"{s.Key}={s.Value}")));

        setters["IsVirtualizing"].Should().Be(true);
        setters["VirtualizationMode"].Should().Be(VirtualizationMode.Recycling);
        setters["ScrollUnit"].Should().Be(ScrollUnit.Pixel);
        setters["CanContentScroll"].Should().Be(true);
        setters["VerticalScrollBarVisibility"].Should().NotBe(
            ScrollBarVisibility.Disabled,
            "a list that does not own its scrolling is measured with infinite height by whatever wraps it");
    }

    /// <summary>
    /// Every chat list is measured against a real height.
    ///
    /// A stack panel and a scroll viewer both offer a child infinite height, and
    /// a panel measured that way realizes every row. Sitting inside one is not
    /// itself the fault: WPF clamps the available size to MaxHeight before
    /// measuring, so a list that caps its own height is bounded wherever it sits.
    /// The fault is being inside one with no cap, which is what the sidebar did.
    /// </summary>
    [Fact]
    public void EveryChatListIsMeasuredAgainstARealHeight()
    {
        var report = Loaded(view => Descendants<ListBox>(view)
            .Select(list => new
            {
                List = Name(list),
                Unbounded = Ancestors(list).Any(a => a is StackPanel or ScrollViewer),
                Capped = !double.IsPositiveInfinity(list.MaxHeight) || !double.IsNaN(list.Height),
            })
            .ToList());

        foreach (var list in report)
        {
            output.WriteLine($"{list.List}: insideUnboundedParent={list.Unbounded} capsItself={list.Capped}");
        }

        report.Should().NotBeEmpty("the sidebar declares the chat lists this asserts about");

        report
            .Where(list => list.Unbounded && !list.Capped)
            .Select(list => list.List)
            .Should().BeEmpty("a list with no cap inside an unbounded parent realizes every row");
    }

    private static string Name(ListBox list) =>
        System.Windows.Data.BindingOperations
            .GetBinding(list, ItemsControl.ItemsSourceProperty)?.Path.Path
        ?? "a chat list";

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
    {
        for (var parent = LogicalTreeHelper.GetParent(node); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
        {
            yield return parent;
        }
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
    /// A dictionary of its own, never the application's. Nothing here needs the
    /// process wide store, so nothing here can leave anything in it.
    /// </summary>
    private static ResourceDictionary Dictionary(string name) => StaRunner.Run(() => new ResourceDictionary
    {
        Source = new Uri($"pack://application:,,,/BetterTranslator;component/{name}.xaml", UriKind.Absolute),
    });

    /// <summary>
    /// Loading a view does need the application's dictionaries, because its
    /// StaticResource lookups walk out to them. Whatever is added is removed
    /// again: Application.Current is process wide, and tests that read tokens
    /// through it start answering differently while these are merged.
    /// </summary>
    private static T Loaded<T>(Func<FrameworkElement, T> read) => StaRunner.Run(() =>
    {
        string[] names =
        [
            "Themes/Colors", "Themes/Typography", "Themes/Metrics", "Themes/Icons",
            "Themes/Flags", "Themes/Controls", "Themes/Markdown", "Themes/Json",
            "Verification/VerificationTokens",
        ];

        var added = new List<ResourceDictionary>();

        foreach (var name in names)
        {
            var source = new Uri($"pack://application:,,,/BetterTranslator;component/{name}.xaml", UriKind.Absolute);

            if (Application.Current!.Resources.MergedDictionaries.All(d => d.Source != source))
            {
                var dictionary = new ResourceDictionary { Source = source };

                Application.Current.Resources.MergedDictionaries.Add(dictionary);
                added.Add(dictionary);
            }
        }

        try
        {
            var view = (FrameworkElement)Application.LoadComponent(
                new Uri("/BetterTranslator;component/Views/SidebarView.xaml", UriKind.Relative));

            return read(view);
        }
        finally
        {
            foreach (var dictionary in added)
            {
                Application.Current!.Resources.MergedDictionaries.Remove(dictionary);
            }
        }
    });
}
