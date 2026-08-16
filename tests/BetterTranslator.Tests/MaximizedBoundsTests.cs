using System.Windows;
using BetterTranslator.App.Services;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What a maximized window has to give back.
///
/// Windows maximizes a resizable window to the work area *inflated by the resize
/// frame*, because on an ordinary window that frame is non-client and covers the
/// overhang. WindowChrome hands the whole window rect to the client area, so the
/// overhang stops being frame and becomes content drawn off the screen: measured
/// on a 3440 by 1440 monitor, the window ran from -8,-8 to 3448,1400 against a
/// work area of 0,0 to 3440,1392. Eight pixels on every side, which is exactly
/// ScrollThumbWidth, and the chat's bar sits at the window edge -- so it was not
/// clipped, it was entirely outside the screen.
///
/// The inset is subtracted rather than looked up, and these tests are what says
/// so. SystemParameters.WindowResizeBorderThickness reports 4 against that
/// measured 8, because it leaves out SM_CXPADDEDBORDER; a fix built on it put
/// half the overhang back and left the other half off the screen.
/// </summary>
public sealed class MaximizedBoundsTests
{
    [Fact]
    public void The_overhang_is_taken_off_every_edge()
    {
        // The measured case, from the monitor this was found on.
        var inset = WindowFrame.MaximizedInset(
            WindowState.Maximized,
            window: new ScreenRect(-8, -8, 3448, 1400),
            work: new ScreenRect(0, 0, 3440, 1392),
            scaleX: 1,
            scaleY: 1);

        inset.Should().Be(new Thickness(8));
    }

    [Fact]
    public void A_restored_window_insets_nothing()
    {
        // Restored, the window rect is where it was asked to be and every edge
        // is already on the screen. An inset here would be a visible gap.
        var inset = WindowFrame.MaximizedInset(
            WindowState.Normal,
            window: new ScreenRect(-8, -8, 3448, 1400),
            work: new ScreenRect(0, 0, 3440, 1392),
            scaleX: 1,
            scaleY: 1);

        inset.Should().Be(default(Thickness));
    }

    [Fact]
    public void A_minimized_window_insets_nothing()
    {
        var inset = WindowFrame.MaximizedInset(
            WindowState.Minimized,
            window: new ScreenRect(-8, -8, 3448, 1400),
            work: new ScreenRect(0, 0, 3440, 1392),
            scaleX: 1,
            scaleY: 1);

        inset.Should().Be(default(Thickness));
    }

    [Fact]
    public void The_overhang_is_converted_out_of_pixels_into_device_independent_units()
    {
        // A margin is in DIPs. At 150 percent the frame is twelve pixels, which
        // is eight units; handing the raw twelve to a Thickness would inset the
        // content by eighteen pixels and leave a gap down every edge.
        var inset = WindowFrame.MaximizedInset(
            WindowState.Maximized,
            window: new ScreenRect(-12, -12, 1932, 1092),
            work: new ScreenRect(0, 0, 1920, 1080),
            scaleX: 1.5,
            scaleY: 1.5);

        inset.Should().Be(new Thickness(8));
    }

    [Fact]
    public void A_taskbar_on_the_top_edge_is_taken_off_the_top_alone()
    {
        // Every edge is measured on its own, so where the taskbar sits never
        // has to be worked out.
        var inset = WindowFrame.MaximizedInset(
            WindowState.Maximized,
            window: new ScreenRect(-8, -8, 1928, 1088),
            work: new ScreenRect(0, 40, 1920, 1080),
            scaleX: 1,
            scaleY: 1);

        inset.Should().Be(new Thickness(8, 48, 8, 8));
    }

    [Fact]
    public void A_window_that_reaches_no_further_than_the_work_area_insets_nothing()
    {
        var inset = WindowFrame.MaximizedInset(
            WindowState.Maximized,
            window: new ScreenRect(0, 0, 1920, 1080),
            work: new ScreenRect(0, 0, 1920, 1080),
            scaleX: 1,
            scaleY: 1);

        inset.Should().Be(default(Thickness));
    }

    [Fact]
    public void A_window_smaller_than_the_work_area_never_takes_a_negative_inset()
    {
        // A negative margin stretches content outwards. Nothing should be able
        // to produce one, whatever the window manager reports.
        var inset = WindowFrame.MaximizedInset(
            WindowState.Maximized,
            window: new ScreenRect(10, 10, 1910, 1070),
            work: new ScreenRect(0, 0, 1920, 1080),
            scaleX: 1,
            scaleY: 1);

        inset.Should().Be(default(Thickness));
    }
}
