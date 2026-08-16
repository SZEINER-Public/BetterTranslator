using System.Globalization;
using System.Resources;
using BetterTranslator.Core.Languages;

namespace BetterTranslator.App.Resources;

public static class Strings
{
    private static readonly ResourceManager Manager =
        new("BetterTranslator.App.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), arguments);

    public static string ComposerDisclaimer => Get(nameof(ComposerDisclaimer));

    public static string EntrySourceLabel => Get(nameof(EntrySourceLabel));

    public static string EntryResultLabel => Get(nameof(EntryResultLabel));

    public static string EntryFailed => Get(nameof(EntryFailed));

    public static string EntryRetry => Get(nameof(EntryRetry));

    public static string EntryQueued => Get(nameof(EntryQueued));

    public static string EntryNothingCameBack => Get(nameof(EntryNothingCameBack));

    public static string EntryPreviewToggle => Get(nameof(EntryPreviewToggle));

    public static string EntryExportSource => Get(nameof(EntryExportSource));

    public static string EntryExportResult => Get(nameof(EntryExportResult));

    public static string EntryExportTitle => Get(nameof(EntryExportTitle));

    public static string EntryExportFilter => Get(nameof(EntryExportFilter));

    public static string EntryPreviewExpanded => Get(nameof(EntryPreviewExpanded));

    public static string EntryPreviewCollapsed => Get(nameof(EntryPreviewCollapsed));

    public static string DirectionSourceLabel => Get(nameof(DirectionSourceLabel));

    public static string DirectionTargetLabel => Get(nameof(DirectionTargetLabel));

    public static string DirectionUnknownLanguage => Get(nameof(DirectionUnknownLanguage));

    public static string DirectionSwap => Get(nameof(DirectionSwap));

    public static string DirectionChooseSource => Get(nameof(DirectionChooseSource));

    public static string DirectionChooseTarget => Get(nameof(DirectionChooseTarget));

    public static string? Reason(DirectionVerdict verdict) => verdict.Status switch
    {
        DirectionStatus.Ready => null,
        DirectionStatus.SameLanguage => Get("DirectionSameLanguage"),
        DirectionStatus.UnknownSource => Get("DirectionUnknownSource"),
        DirectionStatus.UnknownTarget => Get("DirectionUnknownTarget"),
        DirectionStatus.SourceUnsupported =>
            Format("DirectionSourceUnsupported", verdict.LanguageName, verdict.ModelName),
        DirectionStatus.TargetUnsupported =>
            Format("DirectionTargetUnsupported", verdict.LanguageName, verdict.ModelName),
        _ => null,
    };
}
