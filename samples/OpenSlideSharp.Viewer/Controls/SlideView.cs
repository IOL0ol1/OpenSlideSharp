using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using OpenSlideSharp.Viewer.Core;

namespace OpenSlideSharp.Viewer.Controls;

/// <summary>
/// Draws a slide from tiles read in the background, and lets the user pan, zoom and draw rectangular selections.
/// </summary>
/// <remarks>
/// <para>
/// Each frame draws the tiles of the level that suits the zoom (<see cref="TileGrid.BestLevelForDownsample"/> of
/// the downsample per device pixel), aligned to device pixels. A tile not loaded yet is stood in for by the cached
/// tiles of coarser levels, scaled up; the missing tiles are requested centre first. Where no cached tile stands in
/// (a slide just opened, or a part of it not seen yet), the tiles two levels up are requested before them, so a
/// low-resolution image shows quickly and is then replaced. A frame never reads the slide: the
/// <see cref="TileLoader"/> reads on its own threads, and each tile it loads schedules one redraw at most.
/// </para>
/// <para>
/// Setting <see cref="Slide"/> builds a new viewport and loading pipeline (tile cache, bitmap cache and loader), and
/// disposes the old ones without waiting for the old slide's reads in flight (see <see cref="OpenPipeline"/>). The
/// view does not own the slide: dispose it after replacing it here. Leaving the visual tree (closing the window)
/// disposes the pipeline too.
/// </para>
/// <para>
/// Input: dragging with the left button (or a finger) pans, and with Shift held draws a selection; one drag at a
/// time, so other buttons or fingers pressed meanwhile are ignored. The wheel zooms around the pointer by 2^(1/4) a
/// notch. Keys, while the view has the focus (it takes it when clicked): the arrows or W, A, S and D pan by 100
/// pixels, + (or =) and - zoom around the centre, Z fits the slide, 1 zooms to one slide pixel per screen pixel and
/// Space centres the slide.
/// </para>
/// </remarks>
internal sealed class SlideView : Control
{
    private const double KeyboardPan = 100;

    // How many levels above the target level the quick stand-ins are read from (see DrawTiles).
    private const int CoarseLevelStep = 2;

    private static readonly double ZoomStep = Math.Pow(2, 0.25);
    private static readonly ImmutableSolidColorBrush SelectionBrush = new(Color.FromRgb(0xFF, 0xD0, 0x00));
    private static readonly ImmutableDashStyle SelectionBeingDrawnDashes = new([4, 4], 0);

    // Tile pixels live from the read until the first draw copies them into a bitmap (or the tile is dropped), so
    // only a screenful or so is out at any time; pooling them spares the large-object heap while zooming.
    private static readonly ArrayPool<byte> PixelPool =
        ArrayPool<byte>.Create(TileGrid.TileSize * TileGrid.TileSize * 4, maxArraysPerBucket: 32);

    private static readonly Action<byte[]> ReturnPixels = pixels => PixelPool.Return(pixels);

    private readonly List<TileKey> _missing = new();
    private readonly List<TileKey> _coarse = new();
    private readonly HashSet<TileKey> _coarseSeen = new();
    private readonly List<(Bitmap Bitmap, Rect Source, Rect Destination)> _coarser = new();
    private OpenSlide? _slide;
    private SlideState? _slideState;
    private TileGrid? _grid;
    private Viewport? _viewport;
    private Pipeline? _pipeline;
    private IBrush _background = Brushes.White;
    private bool _attached;
    private Point? _pointer;
    private DragMode _drag;
    private IPointer? _dragPointer;
    private Point _dragFrom;
    private (double X, double Y) _selectionStart;
    private Selection? _selectionBeingDrawn;
    private (double Scaling, ImmutablePen Solid, ImmutablePen Dashed)? _pens;
    private int _redrawPosted;

    public SlideView()
    {
        Focusable = true;
        ClipToBounds = true;
        Selections.Changed += InvalidateVisual;
    }

    private enum DragMode
    {
        None,
        Pan,
        Select,
    }

    /// <summary>Raised on the UI thread when the viewport or the pointer position changes.</summary>
    public event Action? ViewChanged;

    /// <summary>
    /// Raised on the UI thread, once per slide, when reading a tile fails; the view then reads no more tiles of it,
    /// even after leaving the visual tree and coming back, until <see cref="Slide"/> is set again.
    /// </summary>
    public event Action<Exception>? LoadFailed;

    /// <summary>Gets or sets the slide shown; null shows nothing. The view does not dispose it.</summary>
    public OpenSlide? Slide
    {
        get => _slide;
        set => SetSlide(value);
    }

    /// <summary>Gets what part of the slide is shown, and how large; null without a slide.</summary>
    public Viewport? Viewport => _viewport;

    /// <summary>Gets the selections drawn on the slide; they are cleared when the slide changes.</summary>
    public SelectionList Selections { get; } = new();

    /// <summary>Gets the level-0 point under the pointer; null when the pointer is not over the view or there is no slide.</summary>
    public (double X, double Y)? PointerSlidePosition =>
        _pointer is { } pointer && _viewport is { } viewport && new Rect(Bounds.Size).Contains(pointer)
            ? viewport.ScreenToSlide(pointer.X, pointer.Y)
            : null;

    /// <summary>Gets the number of tiles read from the slide since it was set (diagnostics and tests).</summary>
    public long TileReads => _slideState?.Reads ?? 0;

    /// <summary>
    /// Gets or sets what reads a tile on the loader threads (by default <see cref="ReadTile"/>); a test hook, taken
    /// when the loading pipeline is built.
    /// </summary>
    internal Func<OpenSlide, TileGrid, TileKey, TileImage>? TileReader { get; set; }

    /// <summary>Gets or sets the tile cache budget, taken when the loading pipeline is built.</summary>
    internal long CacheBudgetBytes { get; set; } = TileCache.DefaultBudgetBytes;

    /// <summary>Gets or sets the number of loader threads, taken when the loading pipeline is built.</summary>
    internal int LoaderThreads { get; set; } = Math.Min(4, Environment.ProcessorCount);

    /// <summary>Gets the level the last frame drew.</summary>
    internal int TargetLevel { get; private set; }

    internal TileLoader? Loader => _pipeline?.Loader;

    internal TileCache? Cache => _pipeline?.Cache;

    internal TileBitmapCache? Bitmaps => _pipeline?.Bitmaps;

    /// <summary>Gets the number of tiles queued or being read.</summary>
    internal int PendingTiles => _pipeline?.Loader.Pending ?? 0;

    /// <summary>
    /// Reads a tile into a pooled buffer, which goes back to the pool once a bitmap is built from it or the tile is
    /// dropped (see <see cref="TileImage"/>).
    /// </summary>
    internal static TileImage ReadTile(OpenSlide slide, TileGrid grid, TileKey key)
    {
        (long x0, long y0, int width, int height) = grid.Region(key);
        int length = width * height * 4;
        byte[] pixels = PixelPool.Rent(length);
        try
        {
            slide.ReadRegion(x0, y0, key.Level, width, height, pixels.AsSpan(0, length));
            return new TileImage(width, height, pixels, ReturnPixels);
        }
        catch
        {
            PixelPool.Return(pixels);
            throw;
        }
    }

    /// <summary>Orders tiles by the distance of their centres from the level-0 point (<paramref name="x0"/>, <paramref name="y0"/>).</summary>
    internal static void SortCenterFirst(List<TileKey> keys, TileGrid grid, double x0, double y0)
    {
        keys.Sort((a, b) =>
        {
            int order = Distance(a).CompareTo(Distance(b));
            return order != 0 ? order : a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column);
        });

        double Distance(TileKey key)
        {
            (double left, double top, double right, double bottom) = grid.Bounds(key);
            double dx = ((left + right) / 2) - x0;
            double dy = ((top + bottom) / 2) - y0;
            return (dx * dx) + (dy * dy);
        }
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(_background, new Rect(Bounds.Size));
        if (_viewport is not { ViewWidth: > 0, ViewHeight: > 0 } viewport)
        {
            return;
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (_pipeline is { } pipeline)
        {
            DrawTiles(context, pipeline, viewport, scaling);
        }

        DrawSelections(context, viewport, scaling);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        OpenPipeline();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        ClosePipeline();
        EndDrag();
        _pointer = null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _viewport?.Resize(e.NewSize.Width, e.NewSize.Height);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus(NavigationMethod.Pointer);
        PointerPoint point = e.GetCurrentPoint(this);
        _pointer = point.Position;
        // One drag at a time: a press during one (another finger, say) neither restarts it nor moves its anchor.
        if (_drag != DragMode.None
            || _viewport is not { } viewport
            || point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        _drag = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? DragMode.Select : DragMode.Pan;
        _dragPointer = e.Pointer;
        _dragFrom = point.Position;
        _selectionStart = viewport.ScreenToSlide(point.Position.X, point.Position.Y);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        _pointer = position;
        if (_viewport is { } viewport && e.Pointer == _dragPointer)
        {
            if (_drag == DragMode.Pan)
            {
                Vector moved = position - _dragFrom;
                _dragFrom = position;
                viewport.PanBy(moved.X, moved.Y);
            }
            else if (_drag == DragMode.Select)
            {
                _selectionBeingDrawn = SelectionTo(viewport, position);
                InvalidateVisual();
            }
        }

        ViewChanged?.Invoke();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragMode.None || e.Pointer != _dragPointer || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (_drag == DragMode.Select && _viewport is { } viewport)
        {
            Selection selection = SelectionTo(viewport, e.GetPosition(this));
            if (selection.Width0 > 0 && selection.Height0 > 0)
            {
                Selections.Add(selection);
            }
        }

        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (e.Pointer == _dragPointer)
        {
            EndDrag(); // another control took the pointer: a selection being drawn is dropped
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        ViewChanged?.Invoke();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_viewport is not { } viewport)
        {
            return;
        }

        Point position = e.GetPosition(this);
        _pointer = position;
        viewport.ZoomAt(position.X, position.Y, Math.Pow(2, e.Delta.Y / 4));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Shortcuts with Ctrl, Alt or the platform key belong to the window.
        if (e.Handled || _viewport is not { } viewport
            || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
        {
            return;
        }

        double centerX = viewport.ViewWidth / 2;
        double centerY = viewport.ViewHeight / 2;
        switch (e.Key)
        {
            case Key.Left or Key.A:
                viewport.PanBy(KeyboardPan, 0);
                break;
            case Key.Right or Key.D:
                viewport.PanBy(-KeyboardPan, 0);
                break;
            case Key.Up or Key.W:
                viewport.PanBy(0, KeyboardPan);
                break;
            case Key.Down or Key.S:
                viewport.PanBy(0, -KeyboardPan);
                break;
            case Key.OemPlus or Key.Add:
                viewport.ZoomAt(centerX, centerY, ZoomStep);
                break;
            case Key.OemMinus or Key.Subtract:
                viewport.ZoomAt(centerX, centerY, 1 / ZoomStep);
                break;
            case Key.Z:
                viewport.Fit();
                break;
            case Key.D1 or Key.NumPad1:
                viewport.OneToOne();
                break;
            case Key.Space:
                viewport.Center();
                break;
            default:
                // Keyboard layouts that put these characters on other keys.
                switch (e.KeySymbol)
                {
                    case "+" or "=":
                        viewport.ZoomAt(centerX, centerY, ZoomStep);
                        break;
                    case "-":
                        viewport.ZoomAt(centerX, centerY, 1 / ZoomStep);
                        break;
                    case "1":
                        viewport.OneToOne();
                        break;
                    default:
                        return;
                }

                break;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Returns the slide's background colour, <c>openslide.background-color</c> (RRGGBB in hex); white if it is
    /// missing or malformed.
    /// </summary>
    internal static Color BackgroundColor(IReadOnlyDictionary<string, string> properties) =>
        properties.TryGetValue(OpenSlideProperties.BackgroundColor, out string? value)
        && value.Length == 6
        && uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb)
            ? Color.FromUInt32(0xFF000000 | rgb)
            : Colors.White;

    // The level-0 rectangle on the screen, its edges rounded to device pixels: tiles that share an edge in the
    // slide share it on the screen, with no seam or overlap. (Layout rounding puts the view itself on whole device
    // pixels, so rounding relative to it is enough.)
    private static Rect OnScreen(Viewport viewport, double scaling, double left0, double top0, double right0, double bottom0)
    {
        (double left, double top) = viewport.SlideToScreen(left0, top0);
        (double right, double bottom) = viewport.SlideToScreen(right0, bottom0);
        return new Rect(
            new Point(Math.Round(left * scaling) / scaling, Math.Round(top * scaling) / scaling),
            new Point(Math.Round(right * scaling) / scaling, Math.Round(bottom * scaling) / scaling));
    }

    // The selection on the screen with its edges through the centres of device pixels, so that a line one device
    // pixel wide covers exactly one pixel.
    private static Rect Outline(Viewport viewport, double scaling, Selection selection)
    {
        (double left, double top) = viewport.SlideToScreen(selection.X0, selection.Y0);
        (double right, double bottom) = viewport.SlideToScreen(selection.X0 + selection.Width0, selection.Y0 + selection.Height0);
        return new Rect(new Point(Center(left), Center(top)), new Point(Center(right), Center(bottom)));

        double Center(double x) => (Math.Floor(x * scaling) + 0.5) / scaling;
    }

    private void SetSlide(OpenSlide? slide)
    {
        if (ReferenceEquals(slide, _slide))
        {
            return;
        }

        // Read the new slide first: if that throws, the view keeps the old one.
        TileGrid? grid = null;
        Viewport? viewport = null;
        IBrush background = Brushes.White;
        if (slide is not null)
        {
            grid = new TileGrid(slide.LevelDimensions, slide.LevelDownsamples);
            viewport = new Viewport(slide.Dimensions, slide.LevelDownsamples);
            background = new ImmutableSolidColorBrush(BackgroundColor(slide.Properties));
        }

        ClosePipeline();
        if (_viewport is not null)
        {
            _viewport.Changed -= OnViewportChanged;
        }

        _slide = slide;
        _slideState = slide is null ? null : new SlideState();
        _grid = grid;
        _viewport = viewport;
        _background = background;
        EndDrag();
        Selections.Clear();
        if (viewport is not null)
        {
            viewport.Resize(Bounds.Width, Bounds.Height);
            viewport.Changed += OnViewportChanged;
        }

        OpenPipeline();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    private void OpenPipeline()
    {
        if (_pipeline is not null || !_attached || _slide is not { } slide || _slideState is not { } state
            || _grid is not { } grid)
        {
            return;
        }

        Func<OpenSlide, TileGrid, TileKey, TileImage> read = TileReader ?? ReadTile;
        var cache = new TileCache(CacheBudgetBytes);
        var loader = new TileLoader(
            key =>
            {
                state.CountRead();
                return read(slide, grid, key);
            },
            cache,
            LoaderThreads,
            _ => ScheduleRedraw())
        {
            // Disposing the pipeline (a new slide, File > Close, closing the window) does not wait for the reads in
            // flight, which can take seconds on a slow disk or network share: the UI thread must not freeze. A read
            // that outlives the pipeline is harmless:
            // - its native call holds a lease on the slide's handle (OpenSlideHandle), so disposing the slide meanwhile
            //   defers openslide_close until the call returns, and a read that starts after it throws
            //   ObjectDisposedException, which the disposed loader swallows (no Failed event);
            // - the loader drops a tile read after it was disposed, and the cache disposes a tile added after it was
            //   disposed, so the pixels go back to the pool and nothing reaches the next slide's cache or bitmaps;
            // - the callbacks only post a redraw, or check that their SlideState is still the current one, and count
            //   reads in that SlideState, not in the next slide's.
            DisposeTimeout = TimeSpan.Zero,
        };
        var pipeline = new Pipeline(grid, cache, new TileBitmapCache(), loader);
        loader.Failed += error =>
        {
            // The failure belongs to the slide, not to this loader: a loader built after leaving the visual tree
            // and coming back must not read the slide again, nor report it again.
            state.Failed = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(state, _slideState) && !state.Reported)
                {
                    state.Reported = true;
                    LoadFailed?.Invoke(error);
                }
            });
        };
        _pipeline = pipeline;
        InvalidateVisual();
    }

    private void ClosePipeline()
    {
        Pipeline? pipeline = _pipeline;
        _pipeline = null;
        pipeline?.Dispose();
    }

    // Called on the loader threads: one post at a time, however many tiles arrive before it runs.
    private void ScheduleRedraw()
    {
        if (Interlocked.Exchange(ref _redrawPosted, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _redrawPosted, 0);
                InvalidateVisual();
            });
        }
    }

    private void OnViewportChanged()
    {
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    private void DrawTiles(DrawingContext context, Pipeline pipeline, Viewport viewport, double scaling)
    {
        pipeline.Bitmaps.Sweep();
        TileGrid grid = pipeline.Grid;
        int level = grid.BestLevelForDownsample(viewport.Downsample / scaling);
        TargetLevel = level;
        (double left0, double top0) = viewport.ScreenToSlide(0, 0);
        (double right0, double bottom0) = viewport.ScreenToSlide(viewport.ViewWidth, viewport.ViewHeight);

        // Missing tiles that nothing cached stands in for are first covered from a coarse level, two levels up
        // (4 to 16 times coarser on the usual pyramids, so a few tiles cover a screenful), read before the
        // target tiles: a slide just opened, or a part of it not seen yet, shows a low-resolution image quickly.
        int coarseLevel = Math.Min(level + CoarseLevelStep, grid.LevelCount - 1);
        _missing.Clear();
        _coarse.Clear();
        _coarseSeen.Clear();
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        {
            foreach (TileKey key in grid.VisibleTiles(level, left0, top0, right0, bottom0))
            {
                if (BitmapOf(pipeline, key) is { } bitmap)
                {
                    (double left, double top, double right, double bottom) = grid.Bounds(key);
                    context.DrawImage(bitmap, new Rect(bitmap.Size), OnScreen(viewport, scaling, left, top, right, bottom));
                    continue;
                }

                _missing.Add(key);
                if (!DrawCoarser(context, pipeline, viewport, scaling, key) && coarseLevel > level)
                {
                    (double left, double top, double right, double bottom) = grid.Bounds(key);
                    foreach (TileKey coarse in grid.VisibleTiles(coarseLevel, left, top, right, bottom))
                    {
                        if (_coarseSeen.Add(coarse))
                        {
                            _coarse.Add(coarse);
                        }
                    }
                }
            }
        }

        if (_slideState is { Failed: true })
        {
            return; // reading this slide failed: no more requests for it
        }

        SortCenterFirst(_coarse, grid, viewport.CenterX, viewport.CenterY);
        SortCenterFirst(_missing, grid, viewport.CenterX, viewport.CenterY);
        _coarse.AddRange(_missing);
        pipeline.Loader.Request(_coarse); // also drops what earlier frames queued and this one no longer needs
    }

    /// <summary>
    /// Stands in for a tile not loaded yet with the cached tiles of coarser levels: from the next level up to the
    /// first one whose cached tiles cover the whole tile, drawing coarser levels first so finer ones end up on top.
    /// Returns whether one level covered the whole tile.
    /// </summary>
    private bool DrawCoarser(DrawingContext context, Pipeline pipeline, Viewport viewport, double scaling, TileKey key)
    {
        TileGrid grid = pipeline.Grid;
        (double areaLeft, double areaTop, double areaRight, double areaBottom) = grid.Bounds(key);
        _coarser.Clear();
        bool covered = false;
        for (int level = key.Level + 1; level < grid.LevelCount && !covered; level++)
        {
            covered = true;
            double downsample = grid.LevelDownsample(level);
            foreach (TileKey coarse in grid.VisibleTiles(level, areaLeft, areaTop, areaRight, areaBottom))
            {
                if (BitmapOf(pipeline, coarse) is not { } bitmap)
                {
                    covered = false;
                    continue;
                }

                (double coarseLeft, double coarseTop, double coarseRight, double coarseBottom) = grid.Bounds(coarse);
                double left = Math.Max(areaLeft, coarseLeft);
                double top = Math.Max(areaTop, coarseTop);
                double right = Math.Min(areaRight, coarseRight);
                double bottom = Math.Min(areaBottom, coarseBottom);
                var source = new Rect(
                    (left / downsample) - (coarse.Column * TileGrid.TileSize),
                    (top / downsample) - (coarse.Row * TileGrid.TileSize),
                    (right - left) / downsample,
                    (bottom - top) / downsample);
                _coarser.Add((bitmap, source, OnScreen(viewport, scaling, left, top, right, bottom)));
            }
        }

        for (int i = _coarser.Count - 1; i >= 0; i--)
        {
            context.DrawImage(_coarser[i].Bitmap, _coarser[i].Source, _coarser[i].Destination);
        }

        return covered;
    }

    private Bitmap? BitmapOf(Pipeline pipeline, TileKey key) =>
        pipeline.Cache.TryGet(key, out TileImage? tile) && tile is not null ? pipeline.Bitmaps.Get(key, tile) : null;

    private void DrawSelections(DrawingContext context, Viewport viewport, double scaling)
    {
        if (Selections.Items.Count == 0 && _selectionBeingDrawn is null)
        {
            return;
        }

        if (_pens is not { } pens || pens.Scaling != scaling)
        {
            // One device pixel wide.
            pens = (scaling, new ImmutablePen(SelectionBrush, 1 / scaling), new ImmutablePen(SelectionBrush, 1 / scaling, SelectionBeingDrawnDashes));
            _pens = pens;
        }

        foreach (Selection selection in Selections.Items)
        {
            context.DrawRectangle(null, pens.Solid, Outline(viewport, scaling, selection));
        }

        if (_selectionBeingDrawn is { } drawing)
        {
            context.DrawRectangle(null, pens.Dashed, Outline(viewport, scaling, drawing));
        }
    }

    private Selection SelectionTo(Viewport viewport, Point position)
    {
        (double x, double y) = viewport.ScreenToSlide(position.X, position.Y);
        (double startX, double startY) = _selectionStart;
        return new Selection(Math.Min(startX, x), Math.Min(startY, y), Math.Abs(x - startX), Math.Abs(y - startY));
    }

    private void EndDrag()
    {
        _drag = DragMode.None;
        _dragPointer = null;
        if (_selectionBeingDrawn is not null)
        {
            _selectionBeingDrawn = null;
            InvalidateVisual();
        }
    }

    /// <summary>What the view keeps about the slide while it is set, across loading pipelines.</summary>
    private sealed class SlideState
    {
        // Set on a loader thread when a read fails; read by every frame.
        private volatile bool _failed;
        private long _reads;

        public bool Failed
        {
            get => _failed;
            set => _failed = value;
        }

        /// <summary>Gets the number of tiles read from the slide (on the loader threads).</summary>
        public long Reads => Interlocked.Read(ref _reads);

        public void CountRead() => Interlocked.Increment(ref _reads);

        /// <summary>Gets or sets whether <see cref="LoadFailed"/> was raised (UI thread only).</summary>
        public bool Reported { get; set; }
    }

    /// <summary>The loading machinery of one slide: built when a slide is set, disposed together.</summary>
    private sealed class Pipeline(TileGrid grid, TileCache cache, TileBitmapCache bitmaps, TileLoader loader) : IDisposable
    {
        public TileGrid Grid { get; } = grid;

        public TileCache Cache { get; } = cache;

        public TileBitmapCache Bitmaps { get; } = bitmaps;

        public TileLoader Loader { get; } = loader;

        public void Dispose()
        {
            // The loader first: it starts no more reads and does not wait for those in flight (DisposeTimeout is 0,
            // see OpenPipeline). Disposing the cache then releases the tiles' pixels, and drops the tiles of reads
            // still in flight when they arrive. The bitmaps, which only the UI thread touches, go last.
            Loader.Dispose();
            Cache.Dispose();
            Bitmaps.Dispose();
        }
    }
}
