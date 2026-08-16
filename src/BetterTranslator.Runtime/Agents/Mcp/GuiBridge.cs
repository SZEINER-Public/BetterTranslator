namespace BetterTranslator.Runtime.Agents.Mcp;

public sealed record GuiOutcome(bool Shown, string Detail);

public interface IGuiBridge
{
    Task<GuiOutcome> ShowAsync(Guid? entryId, CancellationToken cancellationToken);

    Task ModelChangedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A row was written by something other than the window. Nothing else tells
    /// it: the chat list is read once at startup, and an agent translation would
    /// otherwise appear only at the next launch.
    /// </summary>
    Task EntryWrittenAsync(Guid chatId, Guid entryId, CancellationToken cancellationToken);
}

public sealed class NoGuiBridge : IGuiBridge
{
    public Task<GuiOutcome> ShowAsync(Guid? entryId, CancellationToken cancellationToken) =>
        Task.FromResult(new GuiOutcome(false, "There is no window to show: this server is running without the application shell."));

    public Task ModelChangedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EntryWrittenAsync(Guid chatId, Guid entryId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
