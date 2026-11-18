using System;
using OpenSlideSharp.Viewer.Core;
using Xunit;

namespace OpenSlideSharp.Viewer.Tests;

public class ViewportTests
{
    // boxes.tiff
    private static readonly SlideSize Boxes = new(300, 250);
    private static readonly double[] BoxesDownsamples = [1.0, 2.0, 4.016129032258064, 8.086312118570184];

    private static Viewport BoxesViewport(double width = 150, double height = 125)
    {
        var viewport = new Viewport(Boxes, BoxesDownsamples);
        viewport.Resize(width, height);
        return viewport;
    }

    [Fact]
    public void Viewport_Fit_CentersWholeSlide()
    {
        // The initial state fits the slide to the view and centres it.
        Viewport viewport = BoxesViewport();
        Assert.Equal(2, viewport.Downsample);
        Assert.Equal((150.0, 125.0), (viewport.CenterX, viewport.CenterY));
        Assert.Equal((150.0, 125.0), (viewport.ViewWidth, viewport.ViewHeight));

        viewport.ZoomAt(10, 20, 3);
        viewport.PanBy(40, -15);
        int changes = 0;
        viewport.Changed += () => changes++;
        viewport.Fit();

        Assert.Equal(1, changes);
        Assert.Equal(2, viewport.Downsample);
        Assert.Equal((150.0, 125.0), (viewport.CenterX, viewport.CenterY));
        Assert.Equal((0.0, 0.0), viewport.ScreenToSlide(0, 0));
        Assert.Equal((300.0, 250.0), viewport.ScreenToSlide(150, 125));
        Assert.Equal((75.0, 62.5), viewport.SlideToScreen(150, 125));
    }

    [Fact]
    public void Viewport_ZoomAt_KeepsPointUnderCursor()
    {
        Viewport viewport = BoxesViewport(400, 300);
        double fit = viewport.Downsample;
        (double X, double Y) before = viewport.ScreenToSlide(30, 40);

        viewport.ZoomAt(30, 40, 1.5);

        Assert.Equal(fit / 1.5, viewport.Downsample, 1e-12);
        (double X, double Y) after = viewport.ScreenToSlide(30, 40);
        Assert.Equal(before.X, after.X, 1e-9);
        Assert.Equal(before.Y, after.Y, 1e-9);

        // Zooming out keeps it too, and SlideToScreen is the inverse.
        viewport.ZoomAt(30, 40, 1 / 3.0);
        after = viewport.ScreenToSlide(30, 40);
        Assert.Equal(before.X, after.X, 1e-9);
        Assert.Equal(before.Y, after.Y, 1e-9);
        (double X, double Y) screen = viewport.SlideToScreen(before.X, before.Y);
        Assert.Equal(30, screen.X, 1e-9);
        Assert.Equal(40, screen.Y, 1e-9);
    }

    [Fact]
    public void Viewport_ZoomClampsToLimits()
    {
        Viewport viewport = BoxesViewport();
        (double X, double Y) anchor = viewport.ScreenToSlide(30, 40);

        viewport.ZoomAt(30, 40, 1e6);

        Assert.Equal(0.25, viewport.Limits.MinDownsample);
        Assert.Equal(viewport.Limits.MinDownsample, viewport.Downsample);
        (double X, double Y) after = viewport.ScreenToSlide(30, 40);
        Assert.Equal(anchor.X, after.X, 1e-9);
        Assert.Equal(anchor.Y, after.Y, 1e-9);

        // The far end: the larger of the last level's downsample and the fit downsample, doubled.
        viewport.ZoomAt(30, 40, 1e-6);
        Assert.Equal(8.086312118570184 * 2, viewport.Limits.MaxDownsample);
        Assert.Equal(viewport.Limits.MaxDownsample, viewport.Downsample);

        viewport.Resize(10, 10); // fit downsample max(30, 25) = 30
        Assert.Equal(60, viewport.Limits.MaxDownsample);
        viewport.ZoomAt(5, 5, 1e-6);
        Assert.Equal(60, viewport.Downsample);
    }

    [Fact]
    public void Viewport_OneToOne_SetsDownsample1()
    {
        Viewport viewport = BoxesViewport();
        viewport.PanBy(20, 10);
        (double X, double Y) center = (viewport.CenterX, viewport.CenterY);

        viewport.OneToOne();

        Assert.Equal(1, viewport.Downsample);
        Assert.Equal(center, (viewport.CenterX, viewport.CenterY)); // around the view centre
    }

    [Fact]
    public void Viewport_TinySlide_FitAndClamp()
    {
        var viewport = new Viewport(new SlideSize(16, 16), [1.0]);
        viewport.Resize(800, 600);
        AssertSane(viewport);

        viewport.ZoomAt(100, 100, 0.5);
        viewport.Fit();

        Assert.Equal(0.25, viewport.Downsample);
        Assert.Equal(viewport.Limits.MinDownsample, viewport.Downsample);
        Assert.Equal(2, viewport.Limits.MaxDownsample); // max(1, 16 / 600) * 2
        Assert.Equal((8.0, 8.0), (viewport.CenterX, viewport.CenterY));
        AssertSane(viewport);

        Exercise(viewport);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(300, 250)]
    public void Viewport_NoLevels_Defaults(long width, long height)
    {
        var viewport = new Viewport(new SlideSize(width, height), [1.0]);
        viewport.Resize(0, 0);
        AssertSane(viewport);

        // With no view size, Fit leaves the state alone.
        viewport.ZoomAt(0, 0, 2);
        viewport.PanBy(7, 3);
        (double, double, double) state = (viewport.Downsample, viewport.CenterX, viewport.CenterY);
        viewport.Fit();
        Assert.Equal(state, (viewport.Downsample, viewport.CenterX, viewport.CenterY));

        Exercise(viewport);

        // Nonsense input is ignored rather than turned into NaN.
        viewport.Resize(-5, double.NaN);
        AssertSane(viewport);
        viewport.ZoomAt(0, 0, double.NaN);
        viewport.ZoomAt(0, 0, 0);
        viewport.ZoomAt(0, 0, -2);
        viewport.PanBy(double.PositiveInfinity, 0);
        viewport.CenterOn(double.NaN, 1);
        viewport.ZoomTo(double.NaN, 0, 10, 10);
        AssertSane(viewport);
    }

    [Fact]
    public void Viewport_ZoomTo_FitsRectangle()
    {
        Viewport viewport = BoxesViewport();

        viewport.ZoomTo(100, 50, 60, 25);

        // The wider side (60 / 150 = 0.4 over 25 / 125 = 0.2) decides; the rectangle is centred.
        Assert.Equal(0.4, viewport.Downsample, 1e-12);
        Assert.Equal(130, viewport.CenterX, 1e-9);
        Assert.Equal(62.5, viewport.CenterY, 1e-9);
        (double X, double Y) topLeft = viewport.SlideToScreen(100, 50);
        (double X, double Y) bottomRight = viewport.SlideToScreen(160, 75);
        Assert.Equal(0, topLeft.X, 1e-9);
        Assert.Equal(31.25, topLeft.Y, 1e-9);
        Assert.Equal(150, bottomRight.X, 1e-9);
        Assert.Equal(93.75, bottomRight.Y, 1e-9);

        // A rectangle smaller than the zoom limit allows is centred at the limit.
        viewport.ZoomTo(10, 10, 2, 2);
        Assert.Equal(0.25, viewport.Downsample);
        Assert.Equal((11.0, 11.0), (viewport.CenterX, viewport.CenterY));
    }

    [Fact]
    public void Viewport_PanAndCenter()
    {
        Viewport viewport = BoxesViewport();

        viewport.PanBy(50, -10);
        Assert.Equal((150 - 50 * 2.0, 125 + 10 * 2.0), (viewport.CenterX, viewport.CenterY));

        viewport.CenterOn(30, 40);
        Assert.Equal((30.0, 40.0), (viewport.CenterX, viewport.CenterY));

        viewport.ZoomAt(0, 0, 2);
        double downsample = viewport.Downsample;
        viewport.Center();
        Assert.Equal((150.0, 125.0), (viewport.CenterX, viewport.CenterY));
        Assert.Equal(downsample, viewport.Downsample);

        // After the user zooms, resizing keeps the zoom and the centre instead of fitting again.
        viewport.Resize(300, 200);
        Assert.Equal(downsample, viewport.Downsample);
        Assert.Equal((150.0, 125.0), (viewport.CenterX, viewport.CenterY));
    }

    private static void Exercise(Viewport viewport)
    {
        Action[] operations =
        [
            viewport.Fit,
            viewport.OneToOne,
            viewport.Center,
            () => viewport.CenterOn(5, 5),
            () => viewport.ZoomAt(0, 0, 2),
            () => viewport.ZoomAt(3, 4, 0.5),
            () => viewport.ZoomAt(0, 0, 1e9),
            () => viewport.ZoomAt(0, 0, 1e-9),
            () => viewport.PanBy(10, -10),
            () => viewport.ZoomTo(0, 0, 10, 10),
            () => viewport.ZoomTo(0, 0, 0, 0),
            () => viewport.ZoomTo(1, 1, -4, -4),
            () => viewport.Resize(0, 0),
            () => viewport.Resize(1, 1),
            viewport.Fit,
        ];
        foreach (Action operation in operations)
        {
            operation();
            AssertSane(viewport);
        }
    }

    private static void AssertSane(Viewport viewport)
    {
        (double min, double max) = viewport.Limits;
        AssertFinite(min, max, viewport.Downsample, viewport.CenterX, viewport.CenterY, viewport.ViewWidth, viewport.ViewHeight);
        Assert.True(min > 0 && min <= max, $"limits {min}..{max}");
        Assert.InRange(viewport.Downsample, min, max);
        Assert.True(viewport.ViewWidth >= 0 && viewport.ViewHeight >= 0);
        (double x, double y) = viewport.ScreenToSlide(0, 0);
        (double sx, double sy) = viewport.SlideToScreen(0, 0);
        AssertFinite(x, y, sx, sy);
    }

    private static void AssertFinite(params double[] values)
    {
        foreach (double value in values)
        {
            Assert.True(double.IsFinite(value), $"{value} is not finite");
        }
    }
}
