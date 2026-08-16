using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BetterTranslator.App.Controls;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The token dictionaries are the single source of every colour, size, radius
/// and duration in the application. A key that stops resolving, or a value that
/// drifts from the design table, has to fail here rather than silently fall
/// back at runtime.
/// </summary>
public sealed class DesignTokenTests
{
    private const string PackRoot = "pack://application:,,,/BetterTranslator;component/Themes/";

    private static readonly string[] Dictionaries = ["Colors", "Typography", "Metrics", "Icons"];

    /// <summary>Every colour token and the hex the design table gives it.</summary>
    private static readonly (string Key, string Hex)[] Colours =
    [
        ("BrushDesk", "#FFE8E8E8"),
        ("BrushWindow", "#FFF3F3F3"),
        ("BrushSurface", "#FFFFFFFF"),
        ("BrushSurfaceMuted", "#FFFAFAFA"),
        ("BrushSurfaceSunken", "#FFFCFCFD"),
        ("BrushTrack", "#FFEDEDED"),
        ("BrushTrackAlt", "#FFF0F0F0"),
        ("BrushHover", "#FFF3F3F3"),
        ("BrushHoverStrong", "#FFE2E2E2"),
        ("BrushRowActive", "#FFECECEC"),
        ("BrushText", "#FF1B1B1B"),
        ("BrushTextSecondary", "#FF5D5D5D"),
        ("BrushTextTertiary", "#FF6E6E6E"),
        ("BrushTextMuted", "#FF676767"),
        ("BrushTextSubtle", "#FF6B6B6B"),
        ("BrushIcon", "#FF3B3B3B"),
        ("BrushBorder", "#FFD2D2D2"),
        ("BrushBorderSoft", "#FFE4E4E4"),
        ("BrushBorderFaint", "#FFEFEFEF"),
        ("BrushBorderHairline", "#FFF5F5F5"),
        ("BrushBorderStrong", "#FFB4B4B4"),
        ("BrushWindowEdge", "#FFD4D4D4"),
        ("BrushAccent", "#FF005FB8"),
        ("BrushAccentDeep", "#FF00457E"),
        ("BrushAccentPressed", "#FF013A6B"),
        ("BrushAccentTint", "#FFEFF6FC"),
        ("BrushAccentTintSoft", "#FFF7FBFE"),
        ("BrushAccentBorder", "#FFC4DCF2"),
        ("BrushAccentBorderStrong", "#FF9CC4E6"),
        ("BrushSelection", "#FFCFE4F7"),
        ("BrushSuccess", "#FF2E9E5B"),
        ("BrushSuccessDeep", "#FF2E7D51"),
        ("BrushWarn", "#FFDCA83A"),
        ("BrushWarnSurface", "#FFFDF6E7"),
        ("BrushWarnBorder", "#FFE8D4A8"),
        ("BrushWarnText", "#FF6B4A0F"),
        ("BrushDanger", "#FFB42318"),
        ("BrushDangerSurface", "#FFFDF4F3"),
        ("BrushDangerBorder", "#FFE8B4AE"),
        ("BrushDangerText", "#FF8A1F16"),
        ("BrushChatSource", "#FF5B8DBE"),
        ("BrushRepoSource", "#FF7A6FC0"),
        ("BrushNodeIdle", "#FFC9CFD5"),
        ("BrushProgressTrack", "#FFE9ECEF"),
        ("BrushOverlay", "#571B1B1B"),
        ("BrushBadgeOverlay", "#D11B1B1B"),
    ];

    /// <summary>Type token sizes, in device-independent pixels.</summary>
    private static readonly (string Key, double Size)[] TypeSizes =
    [
        ("TextTitleSize", 16),
        ("TextHeadingSize", 15),
        ("TextChatNameSize", 14.5),
        ("TextSubheadingSize", 14),
        ("TextBodySize", 14),
        ("TextCardTitleSize", 13.5),
        ("TextDefaultSize", 13),
        ("TextSecondarySize", 12.5),
        ("TextMetaSize", 12),
        ("TextFineSize", 11.5),
        ("TextMicroSize", 11),
        ("TextMonoPathSize", 11.5),
        ("TextMonoSmallSize", 10.5),
        ("BodyLineHeight", 20),
        ("ProseLineHeight", 21),
        ("DenseLineHeight", 18),
    ];

    private static readonly string[] TypeStyles =
    [
        "TextTitle", "TextHeading", "TextChatName", "TextSubheading", "TextBody",
        "TextCardTitle", "TextDefault", "TextSecondary", "TextMeta", "TextFine",
        "TextMicro", "TextMonoPath", "TextMonoSmall",
    ];

    private static readonly (string Key, double Value)[] Spacing =
    [
        ("Space1", 2), ("Space2", 4), ("Space3", 5), ("Space4", 6),
        ("Space5", 7), ("Space6", 8), ("Space7", 9), ("Space8", 10),
        ("Space9", 11), ("Space10", 12), ("Space11", 14), ("Space12", 16),
        ("Space13", 18), ("Space14", 20), ("Space15", 24), ("Space16", 28),
    ];

    private static readonly (string Key, double Value)[] Radii =
    [
        ("RadiusXs", 2), ("RadiusSm", 3), ("RadiusMd", 4), ("RadiusControl", 5),
        ("RadiusDefault", 6), ("RadiusRow", 7), ("RadiusPanel", 8), ("RadiusCard", 9),
        ("RadiusCardLg", 10), ("RadiusDialog", 11), ("RadiusComposer", 22),
    ];

    private static readonly (string Key, double Milliseconds)[] Durations =
    [
        ("Motion120", 120), ("Motion130", 130), ("Motion140", 140), ("Motion150", 150),
        ("Motion160", 160), ("Motion170", 170), ("Motion180", 180), ("Motion200", 200),
        ("Motion220", 220), ("Motion240", 240), ("Motion300", 300), ("Motion320", 320),
        ("Motion400", 400), ("Motion900", 900), ("Motion1200", 1200),
    ];

    private static readonly (string Key, double BlurRadius, double Depth, double Opacity)[] Shadows =
    [
        ("ShadowWindow", 34, 14, 0.18),
        ("ShadowPopover", 26, 10, 0.16),
        ("ShadowDialog", 54, 22, 0.24),
        ("ShadowThumb", 2, 1, 0.10),
    ];

    /// <summary>
    /// Every icon in the shipped set. The keys are generated from the SVG file
    /// names, so a rename upstream fails here rather than silently leaving a
    /// blank glyph on screen.
    /// </summary>
    private static readonly string[] Icons =
    [
        "IconArrowRightLong", "IconArrowRightUndo", "IconBoltFast", "IconChatBubbleLarge",
        "IconCheckApplied", "IconCheckCheckbox", "IconCheckDone", "IconCheckTickLang",
        "IconChevronDown", "IconChevronLeft", "IconChevronLeftSmall", "IconChevronRight",
        "IconCloseCrossSearch", "IconCloseSmall", "IconCloseWindow", "IconCodeBrackets",
        "IconCopy", "IconDeleteBin", "IconDownload", "IconEye", "IconEyeFormatted",
        "IconEyePreview", "IconFileGeneric", "IconFolder", "IconFolderLarge", "IconGear",
        "IconImagePlaceholder", "IconInfoCircle", "IconKeyEnter", "IconKeyShift",
        "IconLock", "IconLockSegment",
        "IconMaximize", "IconMemoryGraph", "IconMemoryGraphSmall", "IconMinimize",
        "IconMoreHorizontal", "IconPanelHide", "IconPanelLeft", "IconPanelRight",
        "IconPanelShow", "IconPause", "IconPin", "IconPlay", "IconPlus", "IconPlusSmall",
        "IconRefresh", "IconRename", "IconRepository", "IconRepositorySmall",
        "IconResizeGrip", "IconRestore", "IconSearch", "IconSendArrow", "IconSwapDirection",
        "IconThinkingBubble", "IconWarningCircle", "IconWarningTriangle",
    ];

    [Fact]
    public void EveryDictionaryLoads()
    {
        var loaded = StaRunner.Run(() => Dictionaries.Select(LoadOne).Count(d => d.Count > 0));

        loaded.Should().Be(Dictionaries.Length, "each token dictionary must load and carry entries");
    }

    [Fact]
    public void EveryColourTokenResolvesToItsDocumentedValue()
    {
        var problems = StaRunner.Run(() =>
        {
            var tokens = LoadAll();
            var found = new List<string>();

            foreach (var (key, hex) in Colours)
            {
                if (Lookup(tokens, key) is not SolidColorBrush brush)
                {
                    found.Add($"{key}: missing, or not a SolidColorBrush");
                    continue;
                }

                var actual = brush.Color.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(actual, hex, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add($"{key}: expected {hex}, found {actual}");
                }
            }

            return found;
        });

        problems.Should().BeEmpty();
    }

    [Fact]
    public void SuccessTextTokenIsDarkerThanTheSuccessFill()
    {
        // BrushSuccess scores about 3.0:1 on white and never carries text.
        // BrushSuccessDeep is the one every success string uses.
        var (fillLuminance, textLuminance) = StaRunner.Run(() =>
        {
            var tokens = LoadAll();
            var fill = (SolidColorBrush)Lookup(tokens, "BrushSuccess")!;
            var text = (SolidColorBrush)Lookup(tokens, "BrushSuccessDeep")!;
            return (RelativeLuminance(fill.Color), RelativeLuminance(text.Color));
        });

        textLuminance.Should().BeLessThan(fillLuminance);
        ContrastOnWhite(textLuminance).Should().BeGreaterThanOrEqualTo(4.5,
            "no UI text may score below 4.5:1 against its own background");
    }

    [Fact]
    public void EveryTypeTokenResolvesToItsDocumentedSize()
    {
        var problems = StaRunner.Run(() =>
        {
            var tokens = LoadAll();
            var found = new List<string>();

            foreach (var (key, size) in TypeSizes)
            {
                if (Lookup(tokens, key) is not double actual)
                {
                    found.Add($"{key}: missing, or not a Double");
                }
                else if (Math.Abs(actual - size) > 0.001)
                {
                    found.Add($"{key}: expected {size}, found {actual}");
                }
            }

            foreach (var key in TypeStyles)
            {
                if (Lookup(tokens, key) is not Style style || style.TargetType != typeof(TextBlock))
                {
                    found.Add($"{key}: missing, or not a TextBlock Style");
                }
            }

            if (Lookup(tokens, "FontFamilyUi") is not FontFamily) found.Add("FontFamilyUi: missing");
            if (Lookup(tokens, "FontFamilyMono") is not FontFamily) found.Add("FontFamilyMono: missing");

            return found;
        });

        problems.Should().BeEmpty();
    }

    [Fact]
    public void EverySpacingStepResolvesAsBothADoubleAndAThickness()
    {
        var problems = StaRunner.Run(() =>
        {
            var tokens = LoadAll();
            var found = new List<string>();

            foreach (var (key, value) in Spacing)
            {
                if (Lookup(tokens, key) is not double actual || Math.Abs(actual - value) > 0.001)
                {
                    found.Add($"{key}: expected Double {value}");
                }

                var thicknessKey = key + "Thickness";
                if (Lookup(tokens, thicknessKey) is not Thickness thickness ||
                    Math.Abs(thickness.Left - value) > 0.001 || Math.Abs(thickness.Top - value) > 0.001 ||
                    Math.Abs(thickness.Right - value) > 0.001 || Math.Abs(thickness.Bottom - value) > 0.001)
                {
                    found.Add($"{thicknessKey}: expected uniform Thickness {value}");
                }
            }

            return found;
        });

        problems.Should().BeEmpty();
    }

    [Fact]
    public void EveryRadiusResolvesToItsDocumentedValue()
    {
        var problems = StaRunner.Run(() =>
        {
            var tokens = LoadAll();
            var found = new List<string>();

            foreach (var (key, value) in Radii)
            {
                if (Lookup(tokens, key) is not CornerRadius radius || Math.Abs(radius.TopLeft - value) > 0.001)
                {
                    found.Add($"{key}: expected CornerRadius {value}");
                }
            }

            return found;
        });

        problems.Should().BeEmpty();
    }

    [Fact]
    public void EveryMotionTokenResolves()
    {
        var problems = StaRunner.Run(() =>
        {
            var tokens = LoadAll();
            var found = new List<string>();

            foreach (var (key, milliseconds) in Durations)
            {
                if (Lookup(tokens, key) is not Duration duration || !duration.HasTimeSpan)
                {
                    found.Add($"{key}: missing, or not a Duration");
                }
                else if (Math.Abs(duration.TimeSpan.TotalMilliseconds - milliseconds) > 0.5)
                {
                    found.Add($"{key}: expected {milliseconds} ms, found {duration.TimeSpan.TotalMilliseconds}");
                }
            }

            // WPF has no cubic-bezier easing function; both easings are KeySplines.
            if (Lookup(tokens, "EaseStandard") is not KeySpline standard ||
                standard.ControlPoint1 != new Point(0.2, 0) || standard.ControlPoint2 != new Point(0, 1))
            {
                found.Add("EaseStandard: expected KeySpline 0.2,0 0,1");
            }

            if (Lookup(tokens, "EaseBurst") is not KeySpline burst ||
                burst.ControlPoint1 != new Point(0.3, 0) || burst.ControlPoint2 != new Point(0.4, 1))
            {
                found.Add("EaseBurst: expected KeySpline 0.3,0 0.4,1");
            }

            return found;
        });

        problems.Should().BeEmpty();
    }

    [Fact]
    public void ExactlyTheFourNamedShadowsExistAndCarryTheirDocumentedValues()
    {
        var problems = StaRunner.Run(() =>
        {
            var tokens = LoadAll();
            var found = new List<string>();

            foreach (var (key, blur, depth, opacity) in Shadows)
            {
                if (Lookup(tokens, key) is not System.Windows.Media.Effects.DropShadowEffect shadow)
                {
                    found.Add($"{key}: missing, or not a DropShadowEffect");
                    continue;
                }

                if (Math.Abs(shadow.BlurRadius - blur) > 0.001) found.Add($"{key}: BlurRadius expected {blur}");
                if (Math.Abs(shadow.ShadowDepth - depth) > 0.001) found.Add($"{key}: ShadowDepth expected {depth}");
                if (Math.Abs(shadow.Opacity - opacity) > 0.001) found.Add($"{key}: Opacity expected {opacity}");
                if (Math.Abs(shadow.Direction - 270) > 0.001) found.Add($"{key}: Direction expected 270, straight down");
                if (shadow.Color != Colors.Black) found.Add($"{key}: Color expected Black");
            }

            // No fifth shadow may be introduced.
            var declared = CountOfType<System.Windows.Media.Effects.DropShadowEffect>(tokens);
            if (declared != Shadows.Length)
            {
                found.Add($"expected exactly {Shadows.Length} DropShadowEffect resources, found {declared}");
            }

            return found;
        });

        problems.Should().BeEmpty();
    }

    [Fact]
    public void EveryIconResolvesToADefinitionWithDrawableParts()
    {
        var problems = StaRunner.Run(() =>
        {
            var tokens = LoadAll();
            var found = new List<string>();

            foreach (var key in Icons)
            {
                if (Lookup(tokens, key) is not IconDefinition icon)
                {
                    found.Add($"{key}: missing, or not an IconDefinition");
                    continue;
                }

                if (icon.Box <= 0)
                {
                    found.Add($"{key}: box size is not positive");
                }

                if (icon.Parts.Count == 0)
                {
                    found.Add($"{key}: has no parts");
                }

                if (icon.Parts.Any(p => p.Geometry is null))
                {
                    found.Add($"{key}: a part carries no geometry");
                }

                // The set tunes weights per box size, so a stroked part must
                // carry a real thickness rather than falling back to a default.
                if (icon.Parts.Any(p => !p.Filled && p.StrokeThickness <= 0))
                {
                    found.Add($"{key}: a stroked part has no thickness");
                }
            }

            return found;
        });

        problems.Should().BeEmpty();
    }

    [Fact]
    public void IconWeightsAreNotNormalisedAcrossTheSet()
    {
        // The icon set's README is explicit that weights were tuned per box and
        // must not be flattened to one value. If every icon ever ends up on the
        // same thickness, that tuning has been undone.
        var distinct = StaRunner.Run(() =>
        {
            var tokens = LoadAll();
            return Icons
                .Select(key => Lookup(tokens, key))
                .OfType<IconDefinition>()
                .SelectMany(icon => icon.Parts.Where(p => !p.Filled).Select(p => p.StrokeThickness))
                .Distinct()
                .Count();
        });

        distinct.Should().BeGreaterThan(5, "the set uses stroke weights between 1.0 and 1.8");
    }

    [Fact]
    public void WindowAndLayoutMetricsResolve()
    {
        var problems = StaRunner.Run(() =>
        {
            var tokens = LoadAll();
            var expected = new (string Key, double Value)[]
            {
                ("CaptionHeight", 32),
                ("ToolbarHeight", 48),
                ("WindowDefaultWidth", 1536),
                ("WindowDefaultHeight", 1016),
                ("WindowMinWidth", 880),
                ("WindowMinHeight", 620),
                ("ResizeBorderWidth", 6),
                ("SidebarDefaultWidth", 246),
                ("SidebarMinWidth", 200),
                ("SidebarMaxWidth", 420),
                ("ChatColumnWidth", 788),
                ("BorderWidth", 1),
                ("BorderWidthControl", 1.5),
            };

            return expected
                .Where(e => Lookup(tokens, e.Key) is not double actual || Math.Abs(actual - e.Value) > 0.001)
                .Select(e => $"{e.Key}: expected {e.Value}")
                .ToList();
        });

        problems.Should().BeEmpty();
    }

    /// <summary>
    /// The loading placeholder is laid out from tokens alone -- the control
    /// computes a bar count and a last-bar width and holds no number of its own
    /// -- so a key that stops resolving takes the placeholder's geometry with
    /// it and there is nothing on screen to notice it by.
    /// </summary>
    [Fact]
    public void LoadingPlaceholderMetricsResolve()
    {
        var problems = StaRunner.Run(() =>
        {
            var tokens = LoadAll();
            var expected = new (string Key, double Value)[]
            {
                ("SkeletonRevealDelayMs", 150),
                ("SkeletonMinHoldMs", 350),
                ("SkeletonBarHeight", 10),
                ("SkeletonCharsPerLine", 68),
                ("SkeletonMaxLines", 8),
                ("SkeletonLastBarMinFraction", 0.35),
                ("SkeletonLastBarMaxFraction", 0.85),
                ("SkeletonShimmerDepth", 0.4),
                ("RevealFromOpacity", 0.4),
            };

            var found = expected
                .Where(e => Lookup(tokens, e.Key) is not double actual || Math.Abs(actual - e.Value) > 0.001)
                .Select(e => $"{e.Key}: expected {e.Value}")
                .ToList();

            // The bars sit on the prose line box rather than a pitch of their
            // own. That is what makes the placeholder occupy the lines the
            // translation will, so a bar taller than the box would guarantee the
            // shift the whole arrangement exists to avoid.
            if (Lookup(tokens, "SkeletonBarHeight") is double bar
                && Lookup(tokens, "ProseLineHeight") is double line
                && bar >= line)
            {
                found.Add($"SkeletonBarHeight {bar} must sit inside ProseLineHeight {line}");
            }

            return found;
        });

        problems.Should().BeEmpty();
    }

    private static ResourceDictionary LoadOne(string name) => new()
    {
        Source = new Uri(PackRoot + name + ".xaml", UriKind.Absolute),
    };

    private static ResourceDictionary LoadAll()
    {
        var merged = new ResourceDictionary();
        foreach (var name in Dictionaries)
        {
            merged.MergedDictionaries.Add(LoadOne(name));
        }

        return merged;
    }

    /// <summary>
    /// ResourceDictionary.Contains ignores merged dictionaries, so walk them.
    /// </summary>
    private static object? Lookup(ResourceDictionary dictionary, string key)
    {
        if (dictionary.Contains(key))
        {
            return dictionary[key];
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            var hit = Lookup(merged, key);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static int CountOfType<T>(ResourceDictionary dictionary)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        Collect(dictionary, keys);
        return keys.Count;

        void Collect(ResourceDictionary d, HashSet<string> into)
        {
            foreach (var key in d.Keys)
            {
                if (key is string name && d[key] is T)
                {
                    into.Add(name);
                }
            }

            foreach (var merged in d.MergedDictionaries)
            {
                Collect(merged, into);
            }
        }
    }

    private static double RelativeLuminance(Color colour)
    {
        static double Channel(byte raw)
        {
            var c = raw / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));
    }

    private static double ContrastOnWhite(double luminance) => 1.05 / (luminance + 0.05);
}
