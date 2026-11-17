namespace OpenSlideSharp;

/// <summary>
/// Names of the standard properties OpenSlide defines, usable as keys of <see cref="OpenSlide.Properties"/>.
/// A property is absent if the slide format does not provide it.
/// </summary>
public static class OpenSlideProperties
{
    /// <summary>Background color of the slide, as a hex string such as <c>"FFFFFF"</c>.</summary>
    public const string BackgroundColor = "openslide.background-color";

    /// <summary>Barcode of the slide.</summary>
    public const string Barcode = "openslide.barcode";

    /// <summary>Height in pixels of the non-empty region of level 0.</summary>
    public const string BoundsHeight = "openslide.bounds-height";

    /// <summary>Width in pixels of the non-empty region of level 0.</summary>
    public const string BoundsWidth = "openslide.bounds-width";

    /// <summary>X coordinate of the top-left of the non-empty region of level 0.</summary>
    public const string BoundsX = "openslide.bounds-x";

    /// <summary>Y coordinate of the top-left of the non-empty region of level 0.</summary>
    public const string BoundsY = "openslide.bounds-y";

    /// <summary>Free-form comment embedded in the slide.</summary>
    public const string Comment = "openslide.comment";

    /// <summary>Size in bytes of the slide's ICC color profile.</summary>
    public const string IccSize = "openslide.icc-size";

    /// <summary>Microns per pixel in the X direction at level 0.</summary>
    public const string MppX = "openslide.mpp-x";

    /// <summary>Microns per pixel in the Y direction at level 0.</summary>
    public const string MppY = "openslide.mpp-y";

    /// <summary>Objective magnification of the scanner.</summary>
    public const string ObjectivePower = "openslide.objective-power";

    /// <summary>Hash of the slide's content, stable across file renames.</summary>
    public const string Quickhash1 = "openslide.quickhash-1";

    /// <summary>Name of the format (vendor) that OpenSlide detected, for example <c>"aperio"</c>.</summary>
    public const string Vendor = "openslide.vendor";
}
