using BetterTranslator.App.Services;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// One row in the chat sidebar. The stamp recomputes when the shared clock
/// ticks; the row owns no timer of its own.
/// </summary>
public sealed partial class ChatRowViewModel : ObservableObject
{
    private readonly ClockService _clock;

    public ChatRowViewModel(Chat chat, ClockService clock)
    {
        Chat = chat;
        _clock = clock;
        Name = chat.Name;
        Naming = new SkeletonGate(showing => IsNaming = showing);

        _clock.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClockService.Now))
            {
                OnPropertyChanged(nameof(Stamp));
            }
        };
    }

    public Chat Chat { get; }

    public Guid Id => Chat.Id;

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>
    /// True while the model is naming this chat. The row renders a placeholder
    /// bar on the name's own line box instead of the truncation, so the name
    /// does not sit there looking settled and then change under the reader.
    /// </summary>
    [ObservableProperty]
    public partial bool IsNaming { get; set; }

    /// <summary>
    /// Sizes the placeholder bar. The truncation is what the name is replacing,
    /// so its length is the honest guess at the width of what is coming.
    /// </summary>
    public int NameLength => Name.Length;

    /// <summary>The delay before the placeholder appears and the floor it is held for.</summary>
    public SkeletonGate Naming { get; }

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    /// <summary>
    /// True for the chat that is open. Pinned and Chats are two lists, each
    /// with a selection of its own, so the open row cannot be told by asking a
    /// list which of its items is selected: the other list would go on showing
    /// its last one. One flag across both, held by the workspace.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>
    /// The menu names what the item will do, not what the row currently is, so
    /// a pinned row offers Unpin.
    /// </summary>
    public string PinLabel => IsPinned ? "Unpin" : "Pin to top";

    /// <summary>For example "42 min ago - Jul 28, 20:30".</summary>
    public string Stamp => RelativeTime.Row(Chat.UpdatedAt, _clock.Now);

    /// <summary>For example "Tuesday, 28 July 2026 at 20:29".</summary>
    public string Tooltip => RelativeTime.Tooltip(Chat.UpdatedAt);

    partial void OnNameChanged(string value)
    {
        Chat.Name = value;
        OnPropertyChanged(nameof(NameLength));
    }

    partial void OnIsPinnedChanged(bool value)
    {
        Chat.IsPinned = value;
        OnPropertyChanged(nameof(PinLabel));
    }

    public void Touched()
    {
        OnPropertyChanged(nameof(Stamp));
        OnPropertyChanged(nameof(Tooltip));
    }
}
