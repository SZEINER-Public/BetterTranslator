using Avalonia.Controls;

namespace BetterTranslator.Mac.Parity;

public sealed record ScreenDefinition(string Id, string State, Func<Control> Build);

public static class Screens
{
    public static IReadOnlyList<ScreenDefinition> All() => SeededContent.Register();
}
