using System.Windows;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Runtime.Agents.Mcp;

namespace BetterTranslator.App.Mcp;

public sealed class AppGuiBridge(Func<MainWindowViewModel?> shell) : IGuiBridge
{
    public Task<GuiOutcome> ShowAsync(Guid? entryId, CancellationToken cancellationToken) =>
        OnUiThread(() =>
        {
            var view = shell();

            if (view is null)
            {
                return new GuiOutcome(false, "The application window is not open.");
            }

            var window = Application.Current?.MainWindow;

            if (window is null)
            {
                return new GuiOutcome(false, "The application window is not open.");
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();

            if (entryId is null)
            {
                return new GuiOutcome(true, "Brought the window forward.");
            }

            var row = view.Workspace.Rows.FirstOrDefault(r =>
                view.Workspace.Entries.Any(e => e.Entry.Id == entryId && e.Entry.ChatId == r.Chat.Id));

            if (row is not null)
            {
                view.Workspace.SelectedRow = row;
                return new GuiOutcome(true, $"Selected the chat holding entry {entryId}.");
            }

            return new GuiOutcome(true, $"Brought the window forward. Entry {entryId} is not in the open chat.");
        });

    public Task ModelChangedAsync(CancellationToken cancellationToken) =>
        OnUiThread(() =>
        {
            shell()?.ReloadSettingsFromDisk();
            return true;
        });

    /// <summary>
    /// Only the affected chat, and only its rows. Reloading the whole list would
    /// reselect the first chat and yank the reader out of whatever they were
    /// reading, for a row they may not care about.
    /// </summary>
    public Task EntryWrittenAsync(Guid chatId, Guid entryId, CancellationToken cancellationToken) =>
        OnUiThread(() =>
        {
            var view = shell();

            if (view is null)
            {
                return false;
            }

            _ = view.Workspace.NoticeEntryAsync(chatId);
            return true;
        });

    private static Task<T> OnUiThread<T>(Func<T> work)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            return Task.FromResult(work());
        }

        return dispatcher.CheckAccess()
            ? Task.FromResult(work())
            : dispatcher.InvokeAsync(work).Task;
    }
}
