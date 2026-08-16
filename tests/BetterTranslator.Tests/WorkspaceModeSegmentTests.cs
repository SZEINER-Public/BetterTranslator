using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BetterTranslator.App.Controls;
using BetterTranslator.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Memory is locked, and stays locked.
///
/// The gate is one flag on one segment, which is exactly the kind of thing a
/// later hand removes while doing something else. Nothing but this segment
/// selects WorkspaceMode.Memory, so the flag is the whole of the gate.
///
/// The last case reads the rendered container rather than the style setter.
/// That lesson is already paid for once here: see ScrollBarWidthTests, where a
/// theme minimum beat a width setter and the style could not show it.
/// </summary>
public sealed class WorkspaceModeSegmentTests
{
    [Fact]
    public void Memory_is_locked()
    {
        var problem = StaRunner.Run(() => WithTokens(() =>
        {
            var memory = Segment("Memory");

            memory.IsLocked.Should().BeTrue();
            memory.IsUnlocked.Should().BeFalse();

            return null;
        }));

        problem.Should().BeNull();
    }

    [Fact]
    public void Memory_says_why_it_is_locked_in_its_tooltip_and_its_automation_name()
    {
        var problem = StaRunner.Run(() => WithTokens(() =>
        {
            var memory = Segment("Memory");

            memory.LockedReason.Should().Be("Memory is coming soon");

            // A locked control that reads out as just its label leaves a screen
            // reader at a segment that does nothing, with no reason given.
            memory.AutomationName.Should().Be("Memory. Memory is coming soon");

            return null;
        }));

        problem.Should().BeNull();
    }

    [Fact]
    public void The_other_two_modes_are_open()
    {
        var problem = StaRunner.Run(() => WithTokens(() =>
        {
            foreach (var label in new[] { "Simple", "Advanced" })
            {
                var segment = Segment(label);

                segment.IsLocked.Should().BeFalse($"{label} is not gated");
                segment.LockedReason.Should().BeNull($"{label} has nothing to explain");
            }

            return null;
        }));

        problem.Should().BeNull();
    }

    [Fact]
    public void The_locked_segment_renders_disabled_rather_than_only_being_marked_locked()
    {
        var problem = StaRunner.Run(() => WithTokens(() =>
        {
            var control = new SegmentedControl { ItemsSource = MainWindowViewModel.BuildWorkspaceModes() };

            // A control has to be in a tree and measured before its containers
            // exist; there is nothing to read off an unrealised item.
            var host = new Border { Child = control };
            host.Measure(new Size(1000, 1000));
            host.Arrange(new Rect(0, 0, 1000, 1000));
            control.UpdateLayout();

            var items = control.ItemsSource.Cast<SegmentItem>().ToList();
            var memory = items.First(s => s.Label == "Memory");
            var simple = items.First(s => s.Label == "Simple");

            if (control.ItemContainerGenerator.ContainerFromItem(memory) is not ListBoxItem locked ||
                control.ItemContainerGenerator.ContainerFromItem(simple) is not ListBoxItem open)
            {
                return "the segments did not realise into containers";
            }

            locked.IsEnabled.Should().BeFalse("a locked segment must not be clickable");
            open.IsEnabled.Should().BeTrue("locking Memory must not disable the rest of the control");

            // ShowOnDisabled, or the reason never reaches the pointer that went
            // looking for it.
            ToolTipService.GetShowOnDisabled(locked).Should().BeTrue();
            locked.ToolTip.Should().Be("Memory is coming soon");

            return null;
        }));

        problem.Should().BeNull();
    }

    /// <summary>
    /// The reach of the lock, stated exactly.
    ///
    /// A disabled container takes no click and no focus, which is every way a
    /// reader has of selecting a segment. It does NOT refuse an assignment to
    /// SelectedItem from code -- that was asserted here first and the test
    /// failed, which is the only reason this comment can be trusted. Nothing
    /// assigns it: WorkspaceMode.Memory appears in the segment's Value and in
    /// IsMemoryMode, and nowhere else. If a second writer ever appears, the
    /// gate has to move into SegmentedControl rather than rest on this.
    /// </summary>
    [Fact]
    public void The_locked_segment_shows_the_padlock_and_the_others_do_not()
    {
        var problem = StaRunner.Run(() => WithTokens(() =>
        {
            var control = new SegmentedControl { ItemsSource = MainWindowViewModel.BuildWorkspaceModes() };
            var host = new Border { Child = control };
            host.Measure(new Size(1000, 1000));
            host.Arrange(new Rect(0, 0, 1000, 1000));
            control.UpdateLayout();

            var items = control.ItemsSource.Cast<SegmentItem>().ToList();

            if (control.ItemContainerGenerator.ContainerFromItem(items.First(s => s.Label == "Memory"))
                    is not ListBoxItem locked ||
                control.ItemContainerGenerator.ContainerFromItem(items.First(s => s.Label == "Simple"))
                    is not ListBoxItem open)
            {
                return "the segments did not realise into containers";
            }

            // The glyph is what carries the lock on screen. It hangs off the
            // IsEnabled trigger rather than off IsLocked, so reading IsLocked
            // back would not show that the two are still wired together.
            Padlock(locked).Should().Be(Visibility.Visible, "a locked segment has to look locked");
            Padlock(open).Should().Be(Visibility.Collapsed, "an open segment carries no padlock");

            return null;
        }));

        problem.Should().BeNull();
    }

    private static Visibility Padlock(ListBoxItem container)
    {
        container.ApplyTemplate();

        return container.Template.FindName("Padlock", container) is UIElement padlock
            ? padlock.Visibility
            : Visibility.Hidden;
    }

    private static SegmentItem Segment(string label) =>
        MainWindowViewModel.BuildWorkspaceModes().First(s => s.Label == label);

    private static readonly string[] Dictionaries =
    [
        "Themes/Colors", "Themes/Typography", "Themes/Metrics", "Themes/Icons", "Themes/Controls",
    ];

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
}
