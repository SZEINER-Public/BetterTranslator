using System.IO;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Services;
using BetterTranslator.Indexing;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Attaching the same document twice leaves one chip. Identity is the file's
/// hash, not its name, so a copy saved under another name is still the same
/// document and a rename is not a new one.
/// </summary>
public sealed class AttachmentDedupTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-attach", Guid.NewGuid().ToString("N"));
    private ChatWorkspaceViewModel _workspace = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _workspace = new ChatWorkspaceViewModel(
            new ChatStore(new Database(new AppPaths(_root))),
            new ClockService(),
            _ => null);

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task TheSameFileAttachedTwiceStaysOneChip()
    {
        var path = Write("terms.txt", "keep product names in English");

        await _workspace.AttachAsync([path], CancellationToken.None);
        await _workspace.AttachAsync([path], CancellationToken.None);

        _workspace.Attachments.Should().ContainSingle();
    }

    /// <summary>
    /// The point of hashing rather than comparing names: a copy under another
    /// name is the same document.
    /// </summary>
    [Fact]
    public async Task TheSameContentUnderAnotherNameIsStillOneChip()
    {
        var first = Write("terms.txt", "identical bytes");
        var copy = Write("terms-copy.txt", "identical bytes");

        await _workspace.AttachAsync([first, copy], CancellationToken.None);

        _workspace.Attachments.Should().ContainSingle();
    }

    /// <summary>
    /// And the converse: one name is not enough to make two documents one.
    /// </summary>
    [Fact]
    public async Task DifferentContentIsTwoChips()
    {
        var first = Write("a.txt", "one");
        var second = Write("b.txt", "two");

        await _workspace.AttachAsync([first, second], CancellationToken.None);

        _workspace.Attachments.Should().HaveCount(2);
    }

    [Fact]
    public async Task ADuplicateInsideOneDropIsCollapsed()
    {
        var path = Write("notes.md", "same");

        await _workspace.AttachAsync([path, path, path], CancellationToken.None);

        _workspace.Attachments.Should().ContainSingle();
    }

    /// <summary>
    /// The hash is over bytes, so it does not care what the file is called.
    /// </summary>
    [Fact]
    public async Task TheHashIsOverContentNotTheName()
    {
        var a = Write("x.txt", "shared");
        var b = Write("y.txt", "shared");

        var first = await ContentHash.OfFileAsync(a, CancellationToken.None);
        var second = await ContentHash.OfFileAsync(b, CancellationToken.None);

        first.Should().Be(second).And.NotBeEmpty();
    }
}
