using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// Expanding a file entry has to announce every flag it moved.
///
/// The layers of the result region sit on top of one another in one Grid cell and
/// are told apart only by their Visibility bindings. A flag that changes without
/// a PropertyChanged keeps the value the binding last read, so the layer stays on
/// screen underneath the one that replaced it: a translated Markdown file opened
/// from the chat rendered its raw text and its formatted blocks at the same time,
/// overlapping. The source column was correct, which is what named the culprit --
/// its flag was the one being raised.
/// </summary>
public sealed class EntrySurfaceNotificationTests(ITestOutputHelper output)
{
    [Fact]
    public void ExpandingAFileAnnouncesEveryFlagItChanges()
    {
        var view = new EntryViewModel(MarkdownFile());

        var before = Flags(view);
        var announced = new HashSet<string>(StringComparer.Ordinal);

        view.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name)
            {
                announced.Add(name);
            }
        };

        view.ToggleExpandCommand.Execute(null);

        var after = Flags(view);

        var moved = before
            .Where(pair => after[pair.Key] != pair.Value)
            .Select(pair => pair.Key)
            .ToList();

        output.WriteLine("changed: " + string.Join(", ", moved));
        output.WriteLine("silent:  " + string.Join(", ", moved.Where(m => !announced.Contains(m))));

        moved.Should().NotBeEmpty("expanding a file entry changes what the row shows");
        moved.Where(m => !announced.Contains(m)).Should().BeEmpty(
            "a flag that moved without a PropertyChanged leaves its layer on screen under the one that replaced it");
    }

    [Fact]
    public void AnExpandedMarkdownFileShowsExactlyOneResultLayer()
    {
        var view = new EntryViewModel(MarkdownFile());

        view.ToggleExpandCommand.Execute(null);

        var layers = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["ShowsResultMarkdown"] = view.ShowsResultMarkdown,
            ["ShowsResultJson"] = view.ShowsResultJson,
            ["ShowsResultSyntax"] = view.ShowsResultSyntax,
            ["ShowsMarkedResult"] = view.ShowsMarkedResult,
            ["ShowsVerifiedResult"] = view.ShowsVerifiedResult,
            ["ShowsPlainResult"] = view.ShowsPlainResult,
        };

        output.WriteLine(string.Join(", ", layers.Where(l => l.Value).Select(l => l.Key)));

        layers.Count(l => l.Value).Should().Be(1, "the result layers share one Grid cell");
    }

    private static Dictionary<string, bool> Flags(EntryViewModel view) =>
        typeof(EntryViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(bool) && p.CanRead && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => p.Name, p => (bool)p.GetValue(view)!, StringComparer.Ordinal);

    private static Entry MarkdownFile() => new()
    {
        Id = Guid.NewGuid(),
        ChatId = Guid.NewGuid(),
        Kind = EntryKind.File,
        FileName = "claude-orchestration-setup.md",
        Source = "# claude-orchestration-setup\n\nA one-time setup engine.\n\n## What is in this bundle\n\nText.\n",
        Result = "# claude-orchestration-setup - instalace\n\nJednorazovy nastroj.\n\n## Co je v tomto balicku\n\nText.\n",
        CreatedAt = DateTimeOffset.Now,
        State = EntryState.Done,
        TargetLanguage = "Czech",
    };
}
