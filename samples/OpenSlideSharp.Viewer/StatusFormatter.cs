using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenSlideSharp.Viewer;

/// <summary>
/// The texts of the status bar, as pure functions. Numbers are formatted the same in every culture, with a decimal
/// point.
/// </summary>
internal static class StatusFormatter
{
    // Sizes from 1000 µm up are shown in millimetres.
    private const double MicrometresPerMillimetre = 1000;

    /// <summary>
    /// Returns the level-0 pixel under the pointer, <c>"x: 1234  y: 567"</c> (rounded down); empty when either
    /// coordinate is missing.
    /// </summary>
    public static string Coordinates(double? x, double? y) =>
        x is { } px && y is { } py && double.IsFinite(px) && double.IsFinite(py)
            ? string.Create(CultureInfo.InvariantCulture, $"x: {(long)Math.Floor(px)}  y: {(long)Math.Floor(py)}")
            : "";

    /// <summary>
    /// Returns the zoom as <c>"1:4.02"</c>, level-0 pixels per screen pixel, followed by the magnification it gives
    /// with the slide's objective, <c>"1:4.02 (9.96×)"</c>, when <paramref name="objectivePower"/> is a positive
    /// number.
    /// </summary>
    public static string Zoom(double downsample, double? objectivePower)
    {
        string zoom = string.Create(CultureInfo.InvariantCulture, $"1:{downsample:0.00}");
        return objectivePower is { } power && IsPositive(power) && IsPositive(downsample)
            ? string.Create(CultureInfo.InvariantCulture, $"{zoom} ({power / downsample:0.00}×)")
            : zoom;
    }

    /// <summary>
    /// Returns the physical size of a level-0 rectangle: <c>"812.5 × 600.0 µm"</c>, or <c>"1.25 × 0.80 mm"</c> once
    /// a side reaches 1000 µm. Empty unless both microns-per-pixel values are positive numbers.
    /// </summary>
    public static string PhysicalSize(double width0, double height0, double? mppX, double? mppY)
    {
        if (mppX is not { } micronsX || mppY is not { } micronsY || !IsPositive(micronsX) || !IsPositive(micronsY)
            || !(double.IsFinite(width0) && width0 >= 0) || !(double.IsFinite(height0) && height0 >= 0))
        {
            return "";
        }

        double width = width0 * micronsX;
        double height = height0 * micronsY;
        // Compared as shown, so that 999.96 µm is not printed as "1000.0 µm".
        return Math.Round(Math.Max(width, height), 1) >= MicrometresPerMillimetre
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{width / MicrometresPerMillimetre:0.00} × {height / MicrometresPerMillimetre:0.00} mm")
            : string.Create(CultureInfo.InvariantCulture, $"{width:0.0} × {height:0.0} µm");
    }

    /// <summary>
    /// Returns the slide property <paramref name="key"/> as a number (such as <c>openslide.mpp-x</c>), or null if
    /// it is missing or is not a positive number.
    /// </summary>
    public static double? Number(IReadOnlyDictionary<string, string> properties, string key) =>
        properties.TryGetValue(key, out string? text)
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        && IsPositive(value)
            ? value
            : null;

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;
}
