using BetterTranslator.Core.Languages;

namespace BetterTranslator.Engine.Languages;

public sealed class DirectionGuard
{
    private readonly LanguageCatalog _catalog;

    public DirectionGuard()
        : this(new LanguageCatalog())
    {
    }

    public DirectionGuard(LanguageCatalog catalog) => _catalog = catalog;

    public DirectionVerdict Inspect(TranslationDirection direction, string? modelId = null, string modelName = "")
    {
        if (direction.HasUnknownSource)
        {
            return new DirectionVerdict(DirectionStatus.UnknownSource);
        }

        if (direction.HasUnknownTarget)
        {
            return new DirectionVerdict(DirectionStatus.UnknownTarget);
        }

        if (direction.IsSameLanguage)
        {
            return new DirectionVerdict(DirectionStatus.SameLanguage)
            {
                LanguageName = direction.Target.Name,
            };
        }

        if (string.IsNullOrEmpty(modelId))
        {
            return DirectionVerdict.Ready;
        }

        var listings = _catalog.For(modelId);

        if (Unsupported(listings, direction.Source.Code))
        {
            return new DirectionVerdict(DirectionStatus.SourceUnsupported)
            {
                LanguageName = direction.Source.Name,
                ModelName = modelName,
            };
        }

        if (Unsupported(listings, direction.Target.Code))
        {
            return new DirectionVerdict(DirectionStatus.TargetUnsupported)
            {
                LanguageName = direction.Target.Name,
                ModelName = modelName,
            };
        }

        return DirectionVerdict.Ready;
    }

    private static bool Unsupported(IReadOnlyList<LanguageListing> listings, LanguageCode code)
    {
        var listing = listings.FirstOrDefault(l =>
            string.Equals(l.Code, code.Value, StringComparison.OrdinalIgnoreCase));

        return listing is not null && !listing.IsAvailable;
    }
}
