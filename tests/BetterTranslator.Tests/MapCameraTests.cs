using BetterTranslator.Map.Camera;
using BetterTranslator.Map.World;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// SKElement reports CanvasSize in device pixels while ActualWidth is in DIPs,
/// so every transform has to survive a device scale as well as a zoom. Hit
/// testing runs against the world model, which means these transforms are what
/// decides whether a click lands on the node under the cursor.
/// </summary>
public sealed class MapCameraTests
{
    private static readonly float[] Zooms = [0.5f, 1f, 2.5f];
    private static readonly float[] DeviceScales = [1f, 1.5f];

    [Fact]
    public void WorldToScreenRoundTripsAtEveryZoomAndDeviceScale()
    {
        var points = new[]
        {
            new SKPoint(0, 0),
            new SKPoint(1300, 850),
            new SKPoint(2600, 1700),
            new SKPoint(137.5f, 942.25f),
        };

        foreach (var deviceScale in DeviceScales)
        {
            foreach (var zoom in Zooms)
            {
                var camera = new MapCamera { DeviceScale = deviceScale };
                camera.ZoomAt(SKPoint.Empty, zoom);
                camera.PanByWorld(213, -87);

                foreach (var world in points)
                {
                    var back = camera.ScreenToWorld(camera.WorldToScreen(world));

                    back.X.Should().BeApproximately(world.X, 0.01f,
                        $"zoom {zoom} at device scale {deviceScale}");
                    back.Y.Should().BeApproximately(world.Y, 0.01f,
                        $"zoom {zoom} at device scale {deviceScale}");
                }
            }
        }
    }

    [Fact]
    public void ScreenToWorldRoundTripsBackToTheSameScreenPoint()
    {
        foreach (var deviceScale in DeviceScales)
        {
            foreach (var zoom in Zooms)
            {
                var camera = new MapCamera { DeviceScale = deviceScale };
                camera.ZoomAt(SKPoint.Empty, zoom);

                var screen = new SKPoint(640.5f, 377.25f);
                var back = camera.WorldToScreen(camera.ScreenToWorld(screen));

                back.X.Should().BeApproximately(screen.X, 0.01f);
                back.Y.Should().BeApproximately(screen.Y, 0.01f);
            }
        }
    }

    [Fact]
    public void ZoomingAtACursorKeepsTheWorldPointUnderIt()
    {
        var camera = new MapCamera { DeviceScale = 1.5f };
        var anchor = new SKPoint(900, 400);

        var before = camera.ScreenToWorld(anchor);
        camera.ZoomAt(anchor, 1.8f);
        var after = camera.ScreenToWorld(anchor);

        after.X.Should().BeApproximately(before.X, 0.01f, "the wheel zooms at the cursor");
        after.Y.Should().BeApproximately(before.Y, 0.01f);
    }

    [Fact]
    public void ZoomIsClampedRatherThanRunningAway()
    {
        var camera = new MapCamera();

        for (var i = 0; i < 60; i++)
        {
            camera.ZoomAt(SKPoint.Empty, 2f);
        }

        camera.Zoom.Should().BeLessThanOrEqualTo(6f);

        for (var i = 0; i < 120; i++)
        {
            camera.ZoomAt(SKPoint.Empty, 0.5f);
        }

        camera.Zoom.Should().BeGreaterThanOrEqualTo(0.15f);
    }

    [Fact]
    public void PanningByADragMovesTheWorldWithTheCursor()
    {
        var camera = new MapCamera { DeviceScale = 2f };
        camera.ZoomAt(SKPoint.Empty, 2f);

        var before = camera.Offset;
        camera.PanByDip(50, -20);

        // Dragging right moves the viewport left over the world.
        camera.Offset.X.Should().BeLessThan(before.X);
        camera.Offset.Y.Should().BeGreaterThan(before.Y);
    }

    [Fact]
    public void FitFramesTheBoundsInTheViewportItIsGiven()
    {
        var camera = new MapCamera { DeviceScale = 1f };
        var bounds = new SKRect(200, 250, 2200, 1500);
        var viewport = new SKSize(1000, 700);

        camera.Fit(viewport, bounds);

        // The centre of the bounds lands in the centre of the viewport.
        var centre = camera.WorldToScreen(new SKPoint(bounds.MidX, bounds.MidY));
        centre.X.Should().BeApproximately(viewport.Width / 2f, 0.5f);
        centre.Y.Should().BeApproximately(viewport.Height / 2f, 0.5f);
    }

    [Fact]
    public void FitUsesTheViewportPassedInEachTime()
    {
        var camera = new MapCamera { DeviceScale = 1f };
        var bounds = new SKRect(0, 0, 2600, 1700);

        camera.Fit(new SKSize(1000, 700), bounds);
        var narrow = camera.Zoom;

        // Leaving the view and returning with a different size must re-frame,
        // not reuse the size from last time.
        camera.Fit(new SKSize(2000, 1400), bounds);

        camera.Zoom.Should().BeGreaterThan(narrow);
    }

    [Fact]
    public void FitAccountsForTheDeviceScale()
    {
        var bounds = new SKRect(0, 0, 2600, 1700);
        var viewport = new SKSize(1000, 700);

        var atOne = new MapCamera { DeviceScale = 1f };
        atOne.Fit(viewport, bounds);

        // The same canvas in device pixels at 150 percent is a smaller viewport
        // in DIPs, so the zoom must come out lower.
        var atOneFive = new MapCamera { DeviceScale = 1.5f };
        atOneFive.Fit(viewport, bounds);

        atOneFive.Zoom.Should().BeLessThan(atOne.Zoom);
    }

    [Fact]
    public void FitIgnoresAnEmptyViewportRatherThanDividingByZero()
    {
        var camera = new MapCamera();
        var before = camera.Zoom;

        camera.Fit(SKSize.Empty, new SKRect(0, 0, 100, 100));

        camera.Zoom.Should().Be(before);
    }

    [Fact]
    public void HitTestingRunsAgainstTheWorldModel()
    {
        var world = new MapWorld();
        var id = Guid.NewGuid();

        world.Nodes.Add(new MapNode
        {
            Id = id,
            Label = "handbook.docx",
            Position = new SKPoint(500, 400),
            Radius = 10,
            Zone = MapZone.ProjectFiles,
        });

        world.HitTest(new SKPoint(503, 402)).Should().NotBeNull();
        world.HitTest(new SKPoint(503, 402))!.Id.Should().Be(id);

        // Well clear of the node: nothing under the cursor.
        world.HitTest(new SKPoint(900, 900)).Should().BeNull();
    }

    [Fact]
    public void TheZoneCaptionsAreTheDocumentedThree()
    {
        MapWorld.ZoneLabels.Values.Should().BeEquivalentTo(["PROJECT FILES", "PROJECT MEMORY", "CHATS"]);
        MapWorld.ZoneLabels[MapZone.Chats].Should().Be("CHATS", "not CHATS AND TRANSLATION MEMORY");
    }

    [Fact]
    public void TheHubIsNamedAfterTheSourceAndNeverCarriesAnId()
    {
        var world = MapWorldBuilder.Build(
            "bubble-desktop",
            [new MapSource("handbook.docx", 24, MapZone.ProjectFiles)],
            totalChunks: 1842);

        var hub = world.Nodes.Single(n => n.IsHub);

        hub.Label.Should().Be("bubble-desktop");

        // The count is grouped in the current culture, which is what the design
        // capture shows, so the expectation is built the same way rather than
        // hardcoding one locale's separator.
        var grouped = 1842.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        hub.Note.Should().Be($"1 source - {grouped} chunks");

        // No label anywhere is a raw identifier.
        foreach (var node in world.Nodes)
        {
            Guid.TryParse(node.Label, out _).Should().BeFalse();
            node.Label.Should().NotContain(node.Id.ToString());
        }
    }

    [Fact]
    public void LooseFilesWithNoProjectStillNameTheHub()
    {
        var world = MapWorldBuilder.Build(
            null,
            [new MapSource("terms.pdf", 3, MapZone.ProjectFiles)],
            totalChunks: 3);

        world.Nodes.Single(n => n.IsHub).Label.Should().Be("No project");
    }

    [Fact]
    public void AnEmptyIndexProducesAnEmptyWorldRatherThanAPlaceholderGraph()
    {
        var world = MapWorldBuilder.Build("bubble-desktop", [], totalChunks: 0);

        world.IsEmpty.Should().BeTrue();
        world.Edges.Should().BeEmpty();
    }

    [Fact]
    public void NodeRadiiStayInTheDocumentedRange()
    {
        var sources = Enumerable.Range(1, 30)
            .Select(i => new MapSource($"file{i}.md", i * 20, MapZone.ProjectFiles))
            .ToList();

        var world = MapWorldBuilder.Build("bubble-desktop", sources, 600);

        world.Nodes.Where(n => !n.IsHub).Should().OnlyContain(n => n.Radius >= 7f && n.Radius <= 18f);
    }
}
