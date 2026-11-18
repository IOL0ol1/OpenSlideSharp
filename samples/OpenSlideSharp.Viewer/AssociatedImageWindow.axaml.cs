using System;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using OpenSlideSharp.Viewer.Controls;

namespace OpenSlideSharp.Viewer;

/// <summary>
/// Shows one associated image of a slide (its label, macro or thumbnail image), scaled to the window with its
/// aspect ratio kept. The window holds a copy of the pixels, so it outlives the slide.
/// </summary>
internal sealed partial class AssociatedImageWindow : Window
{
    // The window starts at the image's size, within these limits.
    private const double MinStartWidth = 240;
    private const double MinStartHeight = 180;
    private const double MaxStartWidth = 1024;
    private const double MaxStartHeight = 768;

    /// <summary>For the designer.</summary>
    public AssociatedImageWindow()
    {
        InitializeComponent();
    }

    /// <summary>Shows <paramref name="image"/>, as read by <see cref="AssociatedImage.Read"/>, titled <paramref name="name"/>.</summary>
    public AssociatedImageWindow(string name, PixelImage image)
        : this()
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(image);
        Title = name;
        Image = image.ToBitmap();
        Picture.Source = Image;
        Width = Math.Clamp(image.Width, MinStartWidth, MaxStartWidth);
        Height = Math.Clamp(image.Height, MinStartHeight, MaxStartHeight);
    }

    /// <summary>Gets the image shown; null for an image without pixels.</summary>
    internal WriteableBitmap? Image { get; }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Picture.Source = null;
        Image?.Dispose();
    }
}
