using System;
using System.Collections.Generic;

namespace OpenSlideSharp.Viewer.Core;

/// <summary>
/// Which part of the slide the view shows, and how large: the level-0 point at the centre of the view and the
/// downsample (level-0 pixels per logical screen pixel). Used on the UI thread only.
/// </summary>
/// <remarks>
/// <para>
/// The downsample stays within <see cref="Limits"/>: from 1/4 (each slide pixel 4 screen pixels wide) to twice the
/// larger of the coarsest level's downsample and the downsample that fits the slide in the view.
/// </para>
/// <para>
/// A new viewport fits the slide to the view and centres it, and keeps doing so as the view is resized until it is
/// zoomed, panned or centred on something else; <see cref="Fit"/> returns to that state. Input that is not a finite
/// number is ignored, so the state never becomes NaN.
/// </para>
/// </remarks>
internal sealed class Viewport
{
    private const double MinDownsample = 0.25;

    private readonly double _slideWidth;
    private readonly double _slideHeight;
    private readonly double _coarsestDownsample;
    private bool _fitted = true;

    public Viewport(SlideSize slideSize, IReadOnlyList<double> levelDownsamples)
    {
        ArgumentNullException.ThrowIfNull(levelDownsamples);
        _slideWidth = Math.Max(0, slideSize.Width);
        _slideHeight = Math.Max(0, slideSize.Height);
        double coarsest = levelDownsamples.Count > 0 ? levelDownsamples[^1] : 1;
        _coarsestDownsample = double.IsFinite(coarsest) && coarsest > 0 ? coarsest : 1;
        Downsample = Clamp(1);
        CenterX = _slideWidth / 2;
        CenterY = _slideHeight / 2;
    }

    /// <summary>Raised after any of the properties changes.</summary>
    public event Action? Changed;

    /// <summary>Gets the number of level-0 pixels per logical screen pixel.</summary>
    public double Downsample { get; private set; }

    /// <summary>Gets the level-0 X coordinate at the centre of the view.</summary>
    public double CenterX { get; private set; }

    /// <summary>Gets the level-0 Y coordinate at the centre of the view.</summary>
    public double CenterY { get; private set; }

    /// <summary>Gets the width of the view in logical pixels.</summary>
    public double ViewWidth { get; private set; }

    /// <summary>Gets the height of the view in logical pixels.</summary>
    public double ViewHeight { get; private set; }

    public (double MinDownsample, double MaxDownsample) Limits =>
        (MinDownsample, Math.Max(MinDownsample, Math.Max(_coarsestDownsample, FitDownsample) * 2));

    // The downsample that shows the whole slide in the view; 0 while the view or the slide is empty.
    private double FitDownsample =>
        ViewWidth > 0 && ViewHeight > 0 ? Math.Max(_slideWidth / ViewWidth, _slideHeight / ViewHeight) : 0;

    /// <summary>Sets the size of the view in logical pixels; anything but a positive number counts as 0.</summary>
    public void Resize(double width, double height)
    {
        width = double.IsFinite(width) && width > 0 ? width : 0;
        height = double.IsFinite(height) && height > 0 ? height : 0;
        bool resized = width != ViewWidth || height != ViewHeight;
        ViewWidth = width;
        ViewHeight = height;
        if (_fitted && width > 0 && height > 0)
        {
            Set(Clamp(FitDownsample), _slideWidth / 2, _slideHeight / 2, fitted: true, force: resized);
        }
        else
        {
            // A larger view lowers the zoom-out limit.
            Set(Clamp(Downsample), CenterX, CenterY, _fitted, force: resized);
        }
    }

    /// <summary>Zooms in by <paramref name="factor"/> (out if it is below 1), keeping the slide point under the given screen point in place.</summary>
    public void ZoomAt(double screenX, double screenY, double factor)
    {
        if (!double.IsFinite(screenX) || !double.IsFinite(screenY) || !double.IsFinite(factor) || factor <= 0)
        {
            return;
        }

        (double x0, double y0) = ScreenToSlide(screenX, screenY);
        double downsample = Clamp(Downsample / factor);
        Set(
            downsample,
            x0 - (screenX - (ViewWidth / 2)) * downsample,
            y0 - (screenY - (ViewHeight / 2)) * downsample,
            fitted: false);
    }

    /// <summary>Moves the slide by the given screen distance: a positive <paramref name="dxScreen"/> moves it to the right.</summary>
    public void PanBy(double dxScreen, double dyScreen)
    {
        if (!double.IsFinite(dxScreen) || !double.IsFinite(dyScreen))
        {
            return;
        }

        Set(Downsample, CenterX - (dxScreen * Downsample), CenterY - (dyScreen * Downsample), fitted: false);
    }

    /// <summary>Shows the whole slide, centred. Does nothing while the view has no size.</summary>
    public void Fit()
    {
        if (ViewWidth > 0 && ViewHeight > 0)
        {
            Set(Clamp(FitDownsample), _slideWidth / 2, _slideHeight / 2, fitted: true);
        }
    }

    /// <summary>Zooms to one slide pixel per logical screen pixel around the centre of the view.</summary>
    public void OneToOne() => Set(Clamp(1), CenterX, CenterY, fitted: false);

    /// <summary>Centres the slide without changing the zoom.</summary>
    public void Center() => Set(Downsample, _slideWidth / 2, _slideHeight / 2, _fitted);

    public void CenterOn(double x0, double y0)
    {
        if (double.IsFinite(x0) && double.IsFinite(y0))
        {
            Set(Downsample, x0, y0, fitted: false);
        }
    }

    /// <summary>Centres the level-0 rectangle and zooms so that it just fits, within <see cref="Limits"/>.</summary>
    public void ZoomTo(double x0, double y0, double width0, double height0)
    {
        if (!double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(width0) || !double.IsFinite(height0))
        {
            return;
        }

        width0 = Math.Max(0, width0);
        height0 = Math.Max(0, height0);
        double downsample = ViewWidth > 0 && ViewHeight > 0
            ? Clamp(Math.Max(width0 / ViewWidth, height0 / ViewHeight))
            : Downsample;
        Set(downsample, x0 + (width0 / 2), y0 + (height0 / 2), fitted: false);
    }

    public (double X, double Y) ScreenToSlide(double sx, double sy) =>
        (CenterX + ((sx - (ViewWidth / 2)) * Downsample), CenterY + ((sy - (ViewHeight / 2)) * Downsample));

    public (double X, double Y) SlideToScreen(double x0, double y0) =>
        (((x0 - CenterX) / Downsample) + (ViewWidth / 2), ((y0 - CenterY) / Downsample) + (ViewHeight / 2));

    private double Clamp(double downsample)
    {
        (double min, double max) = Limits;
        return Math.Clamp(downsample, min, max);
    }

    private void Set(double downsample, double centerX, double centerY, bool fitted, bool force = false)
    {
        _fitted = fitted;
        if (!force && downsample == Downsample && centerX == CenterX && centerY == CenterY)
        {
            return;
        }

        Downsample = downsample;
        CenterX = centerX;
        CenterY = centerY;
        Changed?.Invoke();
    }
}
