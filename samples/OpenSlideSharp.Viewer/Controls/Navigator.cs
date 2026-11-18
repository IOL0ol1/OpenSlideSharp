using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using OpenSlideSharp.Viewer.Core;

namespace OpenSlideSharp.Viewer.Controls;

/// <summary>
/// A small picture of the whole slide with the part the main view shows outlined; clicking or dragging on it
/// centres the view there.
/// </summary>
/// <remarks>
/// The slide is drawn at its own aspect ratio, as wide as the control may be and at most <see cref="MaxImageHeight"/>
/// high (scaled up for a slide smaller than that), from the thumbnail if there is one and as a plain area if not.
/// Coordinates map linearly between that area and level 0, so a thumbnail whose rounded size differs slightly from
/// the slide's ratio is stretched to fit rather than shifting the outline.
/// </remarks>
internal sealed class Navigator : Control
{
    /// <summary>The largest height (and, with no width given, width) the slide is drawn at, in logical pixels.</summary>
    public const double MaxImageHeight = 256;

    /// <summary>The colour of the outline of the view.</summary>
    public static readonly Color ViewColor = Color.FromRgb(0x00, 0xA0, 0xFF);

    private static readonly ImmutablePen ViewPen = new(new ImmutableSolidColorBrush(ViewColor), 2);
    private static readonly ImmutableSolidColorBrush NoThumbnailBrush = new(Color.FromArgb(0x30, 0x80, 0x80, 0x80));

    private SlideSize _slideSize;
    private Viewport? _viewport;
    private bool _dragging;

    public Navigator()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>Gets the picture of the slide, or null if there is none (or no slide).</summary>
    public Bitmap? Thumbnail { get; private set; }

    /// <summary>
    /// Gets where the slide is drawn, in this control's coordinates: as large as fits, centred; empty without a
    /// slide. (Measured to the slide's aspect ratio, the control is normally just that size.)
    /// </summary>
    internal Rect ImageRect
    {
        get
        {
            Size bounds = Bounds.Size;
            double scale = _slideSize.Width > 0 && _slideSize.Height > 0
                ? Math.Min(bounds.Width / _slideSize.Width, bounds.Height / _slideSize.Height)
                : 0;
            if (!(scale > 0))
            {
                return default;
            }

            double width = _slideSize.Width * scale;
            double height = _slideSize.Height * scale;
            return new Rect((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height);
        }
    }

    /// <summary>
    /// Shows a slide of <paramref name="slideSize"/> level-0 pixels, its thumbnail (or none) and the view's
    /// <paramref name="viewport"/>; a null viewport shows nothing. The navigator does not dispose the thumbnail.
    /// </summary>
    public void Show(SlideSize slideSize, Bitmap? thumbnail, Viewport? viewport)
    {
        if (_viewport is not null)
        {
            _viewport.Changed -= InvalidateVisual;
        }

        _viewport = viewport;
        _slideSize = viewport is null ? default : slideSize;
        Thumbnail = viewport is null ? null : thumbnail;
        _dragging = false;
        if (_viewport is not null)
        {
            _viewport.Changed += InvalidateVisual;
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        Rect image = ImageRect;
        if (_viewport is not { } viewport || image.Width <= 0 || image.Height <= 0)
        {
            return;
        }

        if (Thumbnail is { } thumbnail)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                context.DrawImage(thumbnail, new Rect(thumbnail.Size), image);
            }
        }
        else
        {
            context.FillRectangle(NoThumbnailBrush, image);
        }

        // The level-0 rectangle the view shows; the part outside the slide is clipped away.
        (double left0, double top0) = viewport.ScreenToSlide(0, 0);
        (double right0, double bottom0) = viewport.ScreenToSlide(viewport.ViewWidth, viewport.ViewHeight);
        Point topLeft = ToNavigator(image, left0, top0);
        Point bottomRight = ToNavigator(image, right0, bottom0);
        context.DrawRectangle(null, ViewPen, new Rect(topLeft, bottomRight));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_slideSize.Width <= 0 || _slideSize.Height <= 0)
        {
            return default;
        }

        // As wide as allowed and at most MaxImageHeight high, keeping the slide's aspect ratio.
        double maxWidth = double.IsFinite(availableSize.Width) ? availableSize.Width : MaxImageHeight;
        double maxHeight = Math.Min(MaxImageHeight, availableSize.Height);
        double scale = Math.Min(maxWidth / _slideSize.Width, maxHeight / _slideSize.Height);
        return scale > 0 ? new Size(_slideSize.Width * scale, _slideSize.Height * scale) : default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed || _viewport is null)
        {
            return;
        }

        _dragging = true;
        e.Pointer.Capture(this);
        CenterViewOn(point.Position);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            CenterViewOn(e.GetPosition(this));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging && e.InitialPressMouseButton == MouseButton.Left)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    private Point ToNavigator(Rect image, double x0, double y0) =>
        new(image.X + (x0 * image.Width / _slideSize.Width), image.Y + (y0 * image.Height / _slideSize.Height));

    // Centres the view on the slide point under the given point of the navigator, kept on the slide.
    private void CenterViewOn(Point position)
    {
        Rect image = ImageRect;
        if (_viewport is not { } viewport || image.Width <= 0 || image.Height <= 0)
        {
            return;
        }

        double x0 = (position.X - image.X) * _slideSize.Width / image.Width;
        double y0 = (position.Y - image.Y) * _slideSize.Height / image.Height;
        viewport.CenterOn(Math.Clamp(x0, 0, _slideSize.Width), Math.Clamp(y0, 0, _slideSize.Height));
    }
}
