using System.IO;
using BetterTranslator.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// One chip above the composer input. Project memory is always the first chip;
/// the same file attached twice produces two chips, and each removes only
/// itself, which is why identity is a fresh id rather than the path.
/// </summary>
public sealed partial class AttachmentViewModel : ObservableObject
{
    private AttachmentViewModel(string label)
    {
        Label = label;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Label { get; }

    public string? FilePath { get; private init; }

    public bool IsMemory { get; private init; }

    /// <summary>Real size on disk, through the one byte formatter.</summary>
    public string? SizeLabel { get; private init; }

    public bool HasSize => SizeLabel is not null;

    public static AttachmentViewModel Memory() => new("Project memory") { IsMemory = true };

    /// <summary>
    /// What this file's bytes hash to. Two chips carrying the same hash are the
    /// same document whatever they are called, which is what stops a second
    /// copy being attached. Null on the memory chip, which is not a file.
    /// </summary>
    public string? ContentHash { get; private init; }

    [ObservableProperty]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is { Length: > 0 };

    public bool IsTranslatable => FilePath is not null && !HasProblem;

    partial void OnProblemChanged(string? value)
    {
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(IsTranslatable));
    }

    public static AttachmentViewModel File(string path, string? contentHash = null)
    {
        var info = new FileInfo(path);

        return new AttachmentViewModel(info.Name)
        {
            FilePath = path,
            SizeLabel = info.Exists ? ByteSize.Format(info.Length) : null,
            ContentHash = contentHash,
        };
    }
}
