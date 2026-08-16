using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BetterTranslator.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The two scopes that pick a path are locked; None is not.
///
/// None stays open on purpose. It is the setting in force and the one the app
/// runs on, so the menu still states the present setting rather than offering
/// nothing that can be chosen.
///
/// These read the row rather than the window, because the row is where the gate
/// is declared. What the row does to the menu button -- disabled, padlocked,
/// tooltip -- is markup, and the reach of that is written down in R51: a
/// disabled control refuses a click and a focus, not an assignment from code.
/// Which is exactly why ChooseScope checks the flag itself, and why that check
/// is tested here rather than assumed.
/// </summary>
public sealed class ProjectScopeLockTests
{
    [Theory]
    [InlineData(ProjectScopeKind.Project, "Project", "Project is coming soon")]
    [InlineData(ProjectScopeKind.Folder, "Folder", "Folder is coming soon")]
    public void A_locked_scope_says_why_in_its_tooltip_and_its_automation_name(
        ProjectScopeKind kind,
        string title,
        string reason)
    {
        var row = new ProjectScopeViewModel(kind) { IsLocked = true, LockedReason = reason };

        row.Title.Should().Be(title);
        row.IsUnlocked.Should().BeFalse();

        // A locked row that reads out as just its label leaves a screen reader
        // at something that does nothing, with no reason given.
        row.AutomationName.Should().Be($"{title}. {reason}");
    }

    [Fact]
    public void An_open_scope_reads_out_as_its_label_alone()
    {
        var row = new ProjectScopeViewModel(ProjectScopeKind.None);

        row.IsLocked.Should().BeFalse();
        row.IsUnlocked.Should().BeTrue();
        row.AutomationName.Should().Be("None");
        row.LockedReason.Should().BeNull();
    }

    [Fact]
    public void Locking_a_row_after_the_fact_republishes_what_depends_on_it()
    {
        // IsUnlocked drives IsEnabled on the menu button and AutomationName
        // drives what is read out. Neither is a stored field, so both have to be
        // raised by hand or a row locked later stays live on screen.
        var row = new ProjectScopeViewModel(ProjectScopeKind.Project);
        var raised = new List<string?>();

        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.IsLocked = true;
        row.LockedReason = "Project is coming soon";

        raised.Should().Contain(nameof(ProjectScopeViewModel.IsUnlocked));
        raised.Should().Contain(nameof(ProjectScopeViewModel.AutomationName));
        row.AutomationName.Should().Be("Project. Project is coming soon");
    }

    [Fact]
    public void The_current_setting_note_still_leads_the_line()
    {
        // Locking must not disturb what the menu says about the setting in
        // force; the two flags are independent.
        var none = new ProjectScopeViewModel(ProjectScopeKind.None) { IsCurrent = true };

        none.Detail.Should().StartWith("Current setting. ");
    }

    /// <summary>
    /// The first run's two scope tiles are locked as well.
    ///
    /// That screen is the other way into a scope: its Folder tile used to run
    /// ChooseScope directly, around the menu. It is also not reachable in a
    /// build as it ships -- the modal opens only when nothing is installed and
    /// the catalogue carries a download URL, and under D7 it carries none -- so
    /// the markup is read here rather than clicked. A tile that quietly loses
    /// its lock would otherwise go unnoticed until the day the modal opens.
    /// </summary>
    [Theory]
    [InlineData("Repository", "Repository is coming soon")]
    [InlineData("Folder", "Folder is coming soon")]
    public void The_first_run_scope_tiles_are_locked(string parameter, string reason)
    {
        var problem = StaRunner.Run(() => WithThemes(() =>
        {
            var view = (FrameworkElement)Application.LoadComponent(
                new Uri("/BetterTranslator;component/Views/FirstRunView.xaml", UriKind.Relative));

            var tile = Descendants(view)
                .OfType<Button>()
                .FirstOrDefault(b => Equals(b.CommandParameter, parameter));

            if (tile is null)
            {
                return $"no start tile carrying CommandParameter {parameter}";
            }

            tile.IsEnabled.Should().BeFalse($"{parameter} leads to a scope that is locked");
            tile.ToolTip.Should().Be(reason);

            // Or the reason never reaches the pointer that went looking for it.
            ToolTipService.GetShowOnDisabled(tile).Should().BeTrue();

            AutomationProperties.GetName(tile).Should().Be($"{parameter}. {reason}");

            return null;
        }));

        problem.Should().BeNull();
    }

    [Fact]
    public void Just_chat_is_left_alone()
    {
        var problem = StaRunner.Run(() => WithThemes(() =>
        {
            var view = (FrameworkElement)Application.LoadComponent(
                new Uri("/BetterTranslator;component/Views/FirstRunView.xaml", UriKind.Relative));

            var tile = Descendants(view)
                .OfType<Button>()
                .FirstOrDefault(b => Equals(b.CommandParameter, "JustChat"));

            if (tile is null)
            {
                return "no Just chat tile";
            }

            tile.IsEnabled.Should().BeTrue("locking the scopes must not take away the way in that works");

            return null;
        }));

        problem.Should().BeNull();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        // The tree is walked logically, not visually: nothing here is rendered,
        // so no visual children exist to walk.
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;

            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static readonly string[] Dictionaries =
    [
        "Themes/Colors", "Themes/Typography", "Themes/Metrics", "Themes/Icons",
        "Themes/Flags", "Themes/Controls", "Themes/Markdown", "Themes/Json",
        "Verification/VerificationTokens",
    ];

    private static string? WithThemes(Func<string?> body)
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
