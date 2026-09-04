using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Engine.Json;
using BetterTranslator.Engine.Markdown;
using BetterTranslator.Engine.Markup;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class RolloutBaselineSnapshotTests
{
    public const string SnapshotRelativePath = "artifacts/rollout/snapshot.txt";

    private static Task<string?> Identity(string text, CancellationToken cancellationToken) => Task.FromResult<string?>(text);

    private static Task<string?> Reversed(string text, CancellationToken cancellationToken)
    {
        var chars = text.ToCharArray();
        Array.Reverse(chars);
        return Task.FromResult<string?>(new string(chars));
    }

    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BetterTranslator.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static async Task<string> Render()
    {
        var builder = new StringBuilder();

        foreach (var (name, text) in SpecCorpus.All)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"document {name} chars {text.Length} sha {Hash(text)}");

            if (name == nameof(SpecCorpus.ResourceJson))
            {
                var identity = await JsonTranslation.TranslateAsync(text, Identity).ConfigureAwait(false);
                var reversed = await JsonTranslation.TranslateAsync(text, Reversed).ConfigureAwait(false);
                builder.AppendLine(CultureInfo.InvariantCulture, $"  json identity translated {identity.Translated} kept {identity.Kept} sha {Hash(identity.Text)}");
                builder.AppendLine(CultureInfo.InvariantCulture, $"  json reversed translated {reversed.Translated} kept {reversed.Kept} sha {Hash(reversed.Text)}");
                continue;
            }

            var markdown = await MarkdownTranslation.TranslateAsync(text, Identity).ConfigureAwait(false);
            var markdownReversed = await MarkdownTranslation.TranslateAsync(text, Reversed).ConfigureAwait(false);
            builder.AppendLine(CultureInfo.InvariantCulture, $"  markdown identity translated {markdown.Translated} recovered {markdown.Recovered} kept {markdown.Kept} issues {markdown.StructureIssues.Count} sha {Hash(markdown.Text)}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"  markdown reversed translated {markdownReversed.Translated} recovered {markdownReversed.Recovered} kept {markdownReversed.Kept} issues {markdownReversed.StructureIssues.Count} sha {Hash(markdownReversed.Text)}");

            foreach (var line in text.Split('\n'))
            {
                var guards = PlaceholderGuard.Protect(line);

                if (!guards.Any)
                {
                    continue;
                }

                var restored = PlaceholderGuard.Restore(guards.Text, guards);
                builder.AppendLine(CultureInfo.InvariantCulture, $"  placeholders {guards.Originals.Count} roundtrip {(string.Equals(restored, line.TrimEnd('\r'), StringComparison.Ordinal) ? "identical" : "changed")} sha {Hash(guards.Text)}");
            }
        }

        return builder.ToString();
    }

    [Fact]
    public async Task Snapshot_of_structure_preserving_paths_is_written_under_artifacts()
    {
        var text = await Render();
        var path = Path.Combine(RepositoryRoot(), SnapshotRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));

        text.Should().Contain("document MixedMarkdown");
        (await Render()).Should().Be(text);
    }
}
