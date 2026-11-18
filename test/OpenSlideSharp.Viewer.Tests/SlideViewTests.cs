using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenSlideSharp.Viewer.Controls;
using OpenSlideSharp.Viewer.Core;
using Xunit;

namespace OpenSlideSharp.Viewer.Tests;

public class SlideViewTests
{
    private const double WindowWidth = 400;
    private const double WindowHeight = 300;

    private static readonly double ZoomStep = Math.Pow(2, 0.25);

    private static OpenSlide Boxes() => OpenSlide.Open(TestFiles.Get("boxes.tiff"));

    [Fact]
    public void SlideView_RendersSlidePixels()
    {
        using OpenSlide slide = Boxes();

        (int coloured, int red, int blue) = HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(slide);
            host.WaitForTiles();
            RenderedFrame frame = host.Capture();
            return (
                frame.Count(c => !RenderedFrame.IsWhite(c)),
                frame.Count(RenderedFrame.IsRed),
                frame.Count(RenderedFrame.IsBlue));
        });

        Assert.True(coloured >= 1000, $"only {coloured} pixels are not background white");
        Assert.True(red > 0 && blue > 0, $"{red} red and {blue} blue pixels");
    }

    [Fact]
    public void SlideView_RenderDoesNotReadOnUiThread()
    {
        using OpenSlide slide = Boxes();
        var readThreads = new ConcurrentQueue<int>();
        using var gate = new ManualResetEventSlim();

        (int uiThread, long reads) = HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(configure: view => view.TileReader = (s, grid, key) =>
            {
                readThreads.Enqueue(Environment.CurrentManagedThreadId);
                gate.Wait(TimeSpan.FromSeconds(30));
                return SlideView.ReadTile(s, grid, key);
            });
            try
            {
                host.View.Slide = slide;
                HeadlessSession.PumpUntil(() => !readThreads.IsEmpty, "a read starts");

                // The reads are held, yet the UI thread goes on rendering frames.
                for (int i = 0; i < 3; i++)
                {
                    host.Capture();
                }

                Assert.True(host.View.PendingTiles > 0, "the reads finished early");
            }
            finally
            {
                gate.Set();
            }

            host.WaitForTiles();
            return (Environment.CurrentManagedThreadId, host.View.TileReads);
        });

        Assert.True(reads > 0);
        Assert.Equal(reads, readThreads.Count);
        Assert.DoesNotContain(uiThread, readThreads);
    }

    [Fact]
    public void SlideView_WheelZoomsAtPointer()
    {
        using OpenSlide slide = Boxes();

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(slide);
            Viewport viewport = host.View.Viewport!;
            var at = new Point(100, 80);
            host.Window.MouseMove(at);
            (double X, double Y) before = host.View.PointerSlidePosition!.Value;
            double downsample = viewport.Downsample;

            host.Window.MouseWheel(at, new Vector(0, 1));

            (double X, double Y) after = host.View.PointerSlidePosition!.Value;
            Assert.InRange(after.X - before.X, -1, 1);
            Assert.InRange(after.Y - before.Y, -1, 1);
            Assert.Equal(downsample / ZoomStep, viewport.Downsample, 9);

            host.Window.MouseWheel(at, new Vector(0, -2)); // out by two notches
            Assert.Equal(downsample * ZoomStep, viewport.Downsample, 9);
            Assert.InRange(host.View.PointerSlidePosition!.Value.X - before.X, -1, 1);
        });
    }

    [Fact]
    public void SlideView_DragPans()
    {
        using OpenSlide slide = Boxes();

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(slide);
            Viewport viewport = host.View.Viewport!;
            (double x, double y, double downsample) = (viewport.CenterX, viewport.CenterY, viewport.Downsample);

            Drag(host.Window, new Point(100, 100), new Point(150, 100));

            Assert.Equal(x - (50 * downsample), viewport.CenterX, 9);
            Assert.Equal(y, viewport.CenterY, 9);
            Assert.Empty(host.View.Selections.Items);

            // Once the button is up, moving does not pan.
            host.Window.MouseMove(new Point(200, 120));
            Assert.Equal(x - (50 * downsample), viewport.CenterX, 9);
            Assert.Equal(y, viewport.CenterY, 9);
        });
    }

    [Fact]
    public void SlideView_ShiftDragAddsSelection()
    {
        using OpenSlide slide = Boxes();

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(slide);
            Viewport viewport = host.View.Viewport!;
            (double x0, double y0) = viewport.ScreenToSlide(100, 80);
            (double x1, double y1) = viewport.ScreenToSlide(220, 200);
            (double centerX, double centerY) = (viewport.CenterX, viewport.CenterY);

            Drag(host.Window, new Point(100, 80), new Point(220, 200), RawInputModifiers.Shift);

            Selection selection = Assert.Single(host.View.Selections.Items);
            AssertSelection(x0, y0, x1 - x0, y1 - y0, selection);
            Assert.Equal((centerX, centerY), (viewport.CenterX, viewport.CenterY)); // drawing does not pan

            // Dragged up and to the left, the rectangle is the same.
            Drag(host.Window, new Point(220, 200), new Point(100, 80), RawInputModifiers.Shift);
            Assert.Equal(2, host.View.Selections.Items.Count);
            AssertSelection(x0, y0, x1 - x0, y1 - y0, host.View.Selections.Items[1]);

            // Without width or height nothing is added.
            Drag(host.Window, new Point(50, 50), new Point(150, 50), RawInputModifiers.Shift);
            Drag(host.Window, new Point(50, 50), new Point(50, 150), RawInputModifiers.Shift);
            Assert.Equal(2, host.View.Selections.Items.Count);
        });

        static void AssertSelection(double x0, double y0, double width0, double height0, Selection actual)
        {
            Assert.Equal(x0, actual.X0, 9);
            Assert.Equal(y0, actual.Y0, 9);
            Assert.Equal(width0, actual.Width0, 9);
            Assert.Equal(height0, actual.Height0, 9);
        }
    }

    [Fact]
    public void SlideView_Keyboard_ZoomFitAndOneToOne()
    {
        using OpenSlide slide = Boxes();

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(slide);
            host.Window.MouseDown(new Point(10, 10), MouseButton.Left);
            host.Window.MouseUp(new Point(10, 10), MouseButton.Left);
            Assert.True(host.View.IsFocused, "a click did not focus the view");
            Viewport viewport = host.View.Viewport!;
            viewport.ZoomAt(50, 50, 3);
            viewport.PanBy(30, 20);

            Press(host.Window, PhysicalKey.Digit1);
            Assert.Equal(1, viewport.Downsample);

            Press(host.Window, PhysicalKey.Z);
            (double, double, double) fitted = (viewport.Downsample, viewport.CenterX, viewport.CenterY);
            Assert.NotEqual(1, fitted.Item1);
            viewport.Fit();
            Assert.Equal(fitted, (viewport.Downsample, viewport.CenterX, viewport.CenterY));
        });
    }

    [Fact]
    public void SlideView_Keyboard_PansZoomsAndCenters()
    {
        using OpenSlide slide = Boxes();

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(slide);
            host.View.Focus();
            Viewport viewport = host.View.Viewport!;
            (double x, double y, double downsample) = (viewport.CenterX, viewport.CenterY, viewport.Downsample);
            double step = 100 * downsample;

            // The arrows and W/A/S/D move the view 100 pixels over the slide.
            Press(host.Window, PhysicalKey.ArrowRight);
            Assert.Equal(x + step, viewport.CenterX, 9);
            Press(host.Window, PhysicalKey.D);
            Assert.Equal(x + (2 * step), viewport.CenterX, 9);
            Press(host.Window, PhysicalKey.ArrowLeft);
            Press(host.Window, PhysicalKey.A);
            Assert.Equal(x, viewport.CenterX, 9);
            Press(host.Window, PhysicalKey.ArrowDown);
            Assert.Equal(y + step, viewport.CenterY, 9);
            Press(host.Window, PhysicalKey.W);
            Press(host.Window, PhysicalKey.ArrowUp);
            Assert.Equal(y - step, viewport.CenterY, 9);
            Press(host.Window, PhysicalKey.S);
            Assert.Equal((x, y), (viewport.CenterX, viewport.CenterY));

            // + (or =) and - zoom by 2^(1/4) around the centre of the view.
            Press(host.Window, PhysicalKey.Equal);
            Assert.Equal(downsample / ZoomStep, viewport.Downsample, 9);
            Press(host.Window, PhysicalKey.Equal, RawInputModifiers.Shift); // +
            Assert.Equal(downsample / ZoomStep / ZoomStep, viewport.Downsample, 9);
            Press(host.Window, PhysicalKey.Minus);
            Press(host.Window, PhysicalKey.NumPadSubtract);
            Assert.Equal(downsample, viewport.Downsample, 9);
            Press(host.Window, PhysicalKey.NumPadAdd);
            Assert.Equal(downsample / ZoomStep, viewport.Downsample, 9);
            Assert.Equal((x, y), (viewport.CenterX, viewport.CenterY));

            // Space centres the slide and keeps the zoom.
            viewport.PanBy(-70, 40);
            double zoomed = viewport.Downsample;
            Press(host.Window, PhysicalKey.Space);
            Assert.Equal((150.0, 125.0, zoomed), (viewport.CenterX, viewport.CenterY, viewport.Downsample));

            // Shortcuts with Ctrl are left to the window.
            Press(host.Window, PhysicalKey.Z, RawInputModifiers.Control);
            Press(host.Window, PhysicalKey.ArrowRight, RawInputModifiers.Control);
            Assert.Equal((150.0, 125.0, zoomed), (viewport.CenterX, viewport.CenterY, viewport.Downsample));
        });
    }

    [Fact]
    public void SlideView_RenderScaling_UsesPhysicalPixels()
    {
        using OpenSlide slide = Boxes();
        (int Left, int Top, int Right, int Bottom) blue0 = SlideBounds(slide, RenderedFrame.IsBlue);

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(slide, scaling: 2);
            host.WaitForTiles();

            RenderedFrame frame = host.Capture();

            Assert.Equal((800, 600), (frame.Width, frame.Height));
            Viewport viewport = host.View.Viewport!;
            Assert.Equal((WindowWidth, WindowHeight), (viewport.ViewWidth, viewport.ViewHeight)); // logical pixels
            (double left, double top) = viewport.SlideToScreen(blue0.Left, blue0.Top);
            (double right, double bottom) = viewport.SlideToScreen(blue0.Right, blue0.Bottom);
            (int Left, int Top, int Right, int Bottom) blue = frame.BoundsOf(RenderedFrame.IsBlue)!.Value;
            Assert.InRange(blue.Left, (left * 2) - 2, (left * 2) + 2);
            Assert.InRange(blue.Top, (top * 2) - 2, (top * 2) + 2);
            Assert.InRange(blue.Right, (right * 2) - 2, (right * 2) + 2);
            Assert.InRange(blue.Bottom, (bottom * 2) - 2, (bottom * 2) + 2);

            // The level is picked per device pixel: 3 slide pixels per logical pixel are 1.5 per device pixel, so
            // level 0 (downsample 1) rather than level 1 (downsample 2).
            viewport.ZoomAt(200, 150, viewport.Downsample / 3);
            host.Capture();
            Assert.Equal(0, host.View.TargetLevel);
            host.Window.SetRenderScaling(1);
            host.Capture();
            Assert.Equal(1, host.View.TargetLevel);
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void SlideView_DrawsSelectionsOneDevicePixelWide(double scaling)
    {
        using OpenSlide slide = Boxes();

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(slide, scaling);
            host.WaitForTiles();
            Viewport viewport = host.View.Viewport!;
            (double x0, double y0) = viewport.ScreenToSlide(60.3, 50.6);
            (double x1, double y1) = viewport.ScreenToSlide(250.2, 200.7);
            RenderedFrame before = host.Capture();
            host.View.Selections.Add(new Selection(x0, y0, x1 - x0, y1 - y0));

            RenderedFrame frame = host.Capture();

            // Each edge covers one device pixel fully and leaves the pixels on either side as they were.
            int left = (int)Math.Floor(60.3 * scaling), right = (int)Math.Floor(250.2 * scaling);
            int top = (int)Math.Floor(50.6 * scaling), bottom = (int)Math.Floor(200.7 * scaling);
            int middleX = (left + right) / 2, middleY = (top + bottom) / 2;
            foreach ((int x, int y, int dx, int dy) in new[] { (left, middleY, 1, 0), (right, middleY, 1, 0), (middleX, top, 0, 1), (middleX, bottom, 0, 1) })
            {
                Assert.True(RenderedFrame.IsSelectionYellow(frame[x, y]), $"({x}, {y}) is {frame[x, y]}");
                Assert.Equal(before[x - dx, y - dy], frame[x - dx, y - dy]);
                Assert.Equal(before[x + dx, y + dy], frame[x + dx, y + dy]);
            }

            // The selection being drawn is dashed; once the button is up it is solid.
            host.Window.MouseDown(new Point(300, 40), MouseButton.Left, RawInputModifiers.Shift);
            host.Window.MouseMove(new Point(380, 280), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
            double dashed = YellowFraction(host.Capture(), (int)Math.Floor(380 * scaling), (int)(60 * scaling), (int)(260 * scaling));
            Assert.InRange(dashed, 0.2, 0.8);
            host.Window.MouseUp(new Point(380, 280), MouseButton.Left, RawInputModifiers.Shift);
            double solid = YellowFraction(host.Capture(), (int)Math.Floor(380 * scaling), (int)(60 * scaling), (int)(260 * scaling));
            Assert.Equal(1.0, solid);
            Assert.Equal(2, host.View.Selections.Items.Count);
        });

        static double YellowFraction(RenderedFrame frame, int x, int top, int bottom) =>
            Enumerable.Range(top, bottom - top).Count(y => RenderedFrame.IsSelectionYellow(frame[x, y])) / (double)(bottom - top);
    }

    [Fact]
    public void SlideView_SwitchSlide_DisposesOldLoader()
    {
        using OpenSlide boxes = Boxes();
        using OpenSlide small = OpenSlide.Open(TestFiles.Get("small.svs"));

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(boxes);
            host.WaitForTiles();
            host.View.Selections.Add(new Selection(10, 10, 20, 20));
            TileLoader oldLoader = host.View.Loader!;
            TileCache oldCache = host.View.Cache!;
            TileBitmapCache oldBitmaps = host.View.Bitmaps!;
            Assert.True(oldCache.Count > 0 && oldBitmaps.Count > 0);
            var key = new TileKey(0, 0, 0);
            Assert.True(oldCache.TryGet(key, out TileImage? tile));
            Bitmap bitmap = oldBitmaps.Get(key, tile!)!;

            host.View.Slide = small;

            Assert.True(oldLoader.IsDisposed);
            Assert.Equal(0, oldCache.Count);
            Assert.Equal(0, oldCache.SizeBytes);
            Assert.Equal(0, oldBitmaps.Count);
            Assert.Throws<ObjectDisposedException>(() => bitmap.PixelSize);
            Assert.NotSame(oldLoader, host.View.Loader);
            Assert.NotSame(oldCache, host.View.Cache);
            Assert.Empty(host.View.Selections.Items); // they belonged to the other slide
            Assert.Equal(0, host.View.TileReads);
            Assert.Equal((8.0, 8.0), (host.View.Viewport!.CenterX, host.View.Viewport.CenterY)); // the 16 x 16 slide, centred

            host.WaitForTiles();
            Assert.True(host.View.Cache!.Count > 0);
        });
    }

    [Fact]
    public void SlideView_DrawsCoarserTilesWhileLoading()
    {
        using OpenSlide slide = Boxes();
        (int Left, int Top, int Right, int Bottom) blue0 = SlideBounds(slide, RenderedFrame.IsBlue);
        using var gate = new ManualResetEventSlim(true);

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(configure: view => view.TileReader = (s, grid, key) =>
            {
                if (key.Level == 0)
                {
                    gate.Wait(TimeSpan.FromSeconds(30));
                }

                return SlideView.ReadTile(s, grid, key);
            });
            try
            {
                // Zoomed all the way out first, so only the coarsest level is read.
                host.View.Slide = slide;
                Viewport viewport = host.View.Viewport!;
                viewport.ZoomAt(200, 150, 1.0 / 16);
                host.WaitForTiles();
                Assert.Equal(slide.LevelCount - 1, host.View.TargetLevel);

                // Back to fit, level 0, whose reads are held: the coarse tiles stand in for them.
                gate.Reset();
                viewport.Fit();
                HeadlessSession.PumpUntil(() => host.View.PendingTiles > 0, "level 0 is requested");
                RenderedFrame frame = host.Capture();

                Assert.Equal(0, host.View.TargetLevel);
                Assert.False(host.View.Cache!.Contains(new TileKey(0, 0, 0)));
                int coloured = frame.Count(c => !RenderedFrame.IsWhite(c));
                Assert.True(coloured >= 1000, $"only {coloured} pixels are not background white");
                (double left, double top) = viewport.SlideToScreen(blue0.Left, blue0.Top);
                (double right, double bottom) = viewport.SlideToScreen(blue0.Right, blue0.Bottom);
                (int Left, int Top, int Right, int Bottom) blue = frame.BoundsOf(RenderedFrame.IsBlue)!.Value;
                double slack = 1 + (slide.LevelDownsamples[^1] / viewport.Downsample); // a coarse pixel, in screen pixels
                Assert.InRange(blue.Left, left - slack, left + slack);
                Assert.InRange(blue.Top, top - slack, top + slack);
                Assert.InRange(blue.Right, right - slack, right + slack);
                Assert.InRange(blue.Bottom, bottom - slack, bottom + slack);
            }
            finally
            {
                gate.Set();
            }

            host.WaitForTiles();
            Assert.True(host.View.Cache!.Contains(new TileKey(0, 0, 0)));
        });
    }

    [Fact]
    public void SlideView_NewSlide_ShowsACoarseLevelBeforeTheTargetLevel()
    {
        using OpenSlide slide = Boxes();
        using var gate = new ManualResetEventSlim();
        var levelsRead = new ConcurrentQueue<int>();

        HeadlessSession.Run(() =>
        {
            // One loader thread, and level 0 held: if a level-0 tile were read first, nothing would ever load.
            using var host = new ViewHost(configure: view =>
            {
                view.LoaderThreads = 1;
                view.TileReader = (s, grid, key) =>
                {
                    levelsRead.Enqueue(key.Level);
                    if (key.Level == 0)
                    {
                        gate.Wait(TimeSpan.FromSeconds(30));
                    }

                    return SlideView.ReadTile(s, grid, key);
                };
            });
            try
            {
                // Fitted to the window, the slide is drawn from level 0, and nothing is cached yet.
                host.View.Slide = slide;
                HeadlessSession.PumpUntil(() => levelsRead.Contains(0), "the coarse tiles are read and level 0 is next");
                RenderedFrame frame = host.Capture();

                Assert.Equal(0, host.View.TargetLevel);
                Assert.NotEqual(0, levelsRead.First());
                Assert.False(host.View.Cache!.Contains(new TileKey(0, 0, 0)));
                int coloured = frame.Count(c => !RenderedFrame.IsWhite(c));
                Assert.True(coloured >= 1000, $"only {coloured} pixels are not background white");
                Assert.True(frame.Count(RenderedFrame.IsRed) > 0 && frame.Count(RenderedFrame.IsBlue) > 0, "the boxes are not drawn");
            }
            finally
            {
                gate.Set();
            }

            host.WaitForTiles();
            Assert.True(host.View.Cache!.Contains(new TileKey(0, 0, 0)));
        });
    }

    [Fact]
    public void SlideView_ReadFailure_StaysReportedWhenTheViewIsAttachedAgain()
    {
        using OpenSlide slide = Boxes();
        var error = new OpenSlideException("corrupt tile");

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(configure: view => view.TileReader = (_, _, _) => throw error);
            int failures = 0;
            host.View.LoadFailed += _ => failures++;
            host.View.Slide = slide;
            HeadlessSession.PumpUntil(() => failures > 0, "the failure is reported");
            long reads = host.View.TileReads;

            // Out of the window and back: a new loader, but the slide is still known to fail.
            host.Window.Content = null;
            HeadlessSession.Pump();
            Assert.Null(host.View.Loader);
            host.Window.Content = host.View;
            for (int i = 0; i < 20; i++)
            {
                HeadlessSession.Pump();
                Thread.Sleep(5);
            }

            Assert.NotNull(host.View.Loader);
            Assert.Equal(1, failures);
            Assert.Equal(reads, host.View.TileReads);

            // Setting the slide again tries it again.
            host.View.Slide = null;
            host.View.Slide = slide;
            HeadlessSession.PumpUntil(() => failures == 2, "the failure is reported again");
        });
    }

    [Fact]
    public void SlideView_AnotherPointerPressed_KeepsTheDrag()
    {
        using OpenSlide slide = Boxes();

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(slide);
            Viewport viewport = host.View.Viewport!;
            (double x, double y, double downsample) = (viewport.CenterX, viewport.CenterY, viewport.Downsample);

            // Panning with one finger: a second one touching down, moving and lifting changes nothing.
            IHeadlessTouchPointer first = host.Window.TouchBegin(new Point(100, 100));
            host.Window.TouchMove(first, new Point(120, 100));
            IHeadlessTouchPointer second = host.Window.TouchBegin(new Point(300, 200));
            host.Window.TouchMove(second, new Point(320, 230));
            Assert.Equal((x - (20 * downsample), y), (viewport.CenterX, viewport.CenterY));
            host.Window.TouchMove(first, new Point(140, 100));
            host.Window.TouchEnd(second, new Point(320, 230));
            host.Window.TouchMove(first, new Point(160, 100));
            host.Window.TouchEnd(first, new Point(160, 100));

            Assert.Equal(x - (60 * downsample), viewport.CenterX, 9);
            Assert.Equal(y, viewport.CenterY, 9);

            // Drawing a selection: the second finger moves neither its start nor its end.
            (double x0, double y0) = viewport.ScreenToSlide(100, 80);
            (double x1, double y1) = viewport.ScreenToSlide(220, 200);
            first = host.Window.TouchBegin(new Point(100, 80), RawInputModifiers.Shift);
            host.Window.TouchMove(first, new Point(150, 120), RawInputModifiers.Shift);
            second = host.Window.TouchBegin(new Point(300, 250), RawInputModifiers.Shift);
            host.Window.TouchMove(second, new Point(310, 260), RawInputModifiers.Shift);
            host.Window.TouchEnd(second, new Point(310, 260), RawInputModifiers.Shift);
            host.Window.TouchMove(first, new Point(220, 200), RawInputModifiers.Shift);
            host.Window.TouchEnd(first, new Point(220, 200), RawInputModifiers.Shift);

            Selection selection = Assert.Single(host.View.Selections.Items);
            Assert.Equal(x0, selection.X0, 9);
            Assert.Equal(y0, selection.Y0, 9);
            Assert.Equal(x1 - x0, selection.Width0, 9);
            Assert.Equal(y1 - y0, selection.Height0, 9);
        });
    }

    [Fact]
    public void SlideView_RapidZoom_KeepsBitmapsWithinTheCache()
    {
        using OpenSlide slide = Boxes();
        const long Budget = 300 * 1024; // both level-0 tiles fit, but not with the coarser ones as well

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(configure: view => view.CacheBudgetBytes = Budget);
            host.View.Slide = slide;
            Viewport viewport = host.View.Viewport!;

            for (int i = 0; i < 40; i++)
            {
                viewport.ZoomAt(200, 150, i % 2 == 0 ? 1.0 / 8 : 8); // out to the coarsest level, back in to level 0
                HeadlessSession.Pump();
            }

            host.WaitForTiles();
            host.Capture(); // a frame sweeps the bitmaps of the tiles evicted before it

            TileCache cache = host.View.Cache!;
            Assert.InRange(cache.SizeBytes, 1, Budget);
            Assert.True(host.View.Bitmaps!.Count <= cache.Count, $"{host.View.Bitmaps.Count} bitmaps for {cache.Count} cached tiles");
        });
    }

    [Fact]
    public void SlideView_ReadFailure_RaisesLoadFailedOnceOnTheUiThread()
    {
        using OpenSlide slide = Boxes();
        var error = new OpenSlideException("corrupt tile");

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(configure: view => view.TileReader = (_, _, _) => throw error);
            var failures = new List<(Exception Error, int Thread)>();
            host.View.LoadFailed += e => failures.Add((e, Environment.CurrentManagedThreadId));

            host.View.Slide = slide;
            HeadlessSession.PumpUntil(() => failures.Count > 0, "the failure is reported");
            long reads = host.View.TileReads;
            host.View.Viewport!.ZoomAt(200, 150, 2); // a new view, but no new requests for this slide
            for (int i = 0; i < 5; i++)
            {
                HeadlessSession.Pump();
            }

            (Exception reported, int thread) = Assert.Single(failures);
            Assert.Same(error, reported);
            Assert.Equal(Environment.CurrentManagedThreadId, thread);
            Assert.Equal(reads, host.View.TileReads);
        });
    }

    [Fact]
    public void SlideView_PointerSlidePosition_FollowsThePointer()
    {
        using OpenSlide slide = Boxes();

        HeadlessSession.Run(() =>
        {
            using var host = new ViewHost(slide);
            int changes = 0;
            host.View.ViewChanged += () => changes++;
            Assert.Null(host.View.PointerSlidePosition);

            host.Window.MouseMove(new Point(200, 150));
            Assert.Equal(host.View.Viewport!.ScreenToSlide(200, 150), host.View.PointerSlidePosition);
            Assert.True(changes > 0, "ViewChanged was not raised");

            host.Window.MouseMove(new Point(WindowWidth + 50, WindowHeight + 50)); // off the view
            Assert.Null(host.View.PointerSlidePosition);

            host.Window.MouseMove(new Point(200, 150));
            host.View.Slide = null;
            Assert.Null(host.View.PointerSlidePosition);
            Assert.Null(host.View.Viewport);
            Assert.Null(host.View.Loader);
            host.Capture(); // draws just the background
        });
    }

    [Fact]
    public void SlideView_RequestsTilesCenterFirst()
    {
        var grid = new TileGrid([new SlideSize(1280, 1280)], [1.0]); // 5 x 5 tiles
        List<TileKey> keys = grid.VisibleTiles(0, 0, 0, 1280, 1280).ToList();

        SlideView.SortCenterFirst(keys, grid, 640, 640);

        Assert.Equal(new TileKey(0, 2, 2), keys[0]);
        double[] distances = keys.Select(Distance).ToArray();
        Assert.Equal(distances.Order(), distances);
        Assert.Equal(25, keys.Distinct().Count());

        double Distance(TileKey key)
        {
            (double left, double top, double right, double bottom) = grid.Bounds(key);
            return Math.Pow(((left + right) / 2) - 640, 2) + Math.Pow(((top + bottom) / 2) - 640, 2);
        }
    }

    [Fact]
    public void SlideView_BackgroundColor_ComesFromTheSlide()
    {
        Assert.Equal(Color.FromRgb(0x10, 0x20, 0x30), SlideView.BackgroundColor(Properties("102030")));
        Assert.Equal(Color.FromRgb(0xAB, 0xCD, 0xEF), SlideView.BackgroundColor(Properties("abcdef")));
        Assert.Equal(Colors.White, SlideView.BackgroundColor(new Dictionary<string, string>()));
        foreach (string malformed in new[] { "", "12345", "1234567", "GG0000", "+12345", " 12345", "0x1234" })
        {
            Assert.Equal(Colors.White, SlideView.BackgroundColor(Properties(malformed)));
        }

        static Dictionary<string, string> Properties(string color) => new() { [OpenSlideProperties.BackgroundColor] = color };
    }

    private static void Drag(Window window, Point from, Point to, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseDown(from, MouseButton.Left, modifiers);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), modifiers | RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, modifiers | RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left, modifiers);
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
    }

    /// <summary>The level-0 rectangle (right and bottom exclusive) holding every pixel of the slide that matches.</summary>
    private static (int Left, int Top, int Right, int Bottom) SlideBounds(OpenSlide slide, Func<(byte R, byte G, byte B), bool> match)
    {
        int width = (int)slide.Dimensions.Width, height = (int)slide.Dimensions.Height;
        var bgra = new byte[width * height * 4];
        slide.ReadRegion(0, 0, 0, width, height, bgra);
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                if (match((bgra[i + 2], bgra[i + 1], bgra[i])))
                {
                    (left, top) = (Math.Min(left, x), Math.Min(top, y));
                    (right, bottom) = (Math.Max(right, x + 1), Math.Max(bottom, y + 1));
                }
            }
        }

        Assert.NotEqual(int.MaxValue, left);
        return (left, top, right, bottom);
    }

    /// <summary>A 400 x 300 window holding a <see cref="SlideView"/>; closing it disposes the view's loader.</summary>
    private sealed class ViewHost : IDisposable
    {
        public ViewHost(OpenSlide? slide = null, double scaling = 1, Action<SlideView>? configure = null)
        {
            View = new SlideView();
            configure?.Invoke(View);
            Window = new Window { Width = WindowWidth, Height = WindowHeight, Content = View };
            Window.Show();
            if (scaling != 1)
            {
                Window.SetRenderScaling(scaling);
            }

            HeadlessSession.Pump(); // lays the view out
            if (slide is not null)
            {
                View.Slide = slide;
            }
        }

        public SlideView View { get; }

        public Window Window { get; }

        /// <summary>Renders until the view has read its tiles and has none left to read.</summary>
        public void WaitForTiles() =>
            HeadlessSession.PumpUntil(() => View.TileReads > 0 && View.PendingTiles == 0, "the visible tiles are loaded");

        public RenderedFrame Capture() => RenderedFrame.Capture(Window);

        public void Dispose()
        {
            Window.Close();
            HeadlessSession.Pump();
        }
    }
}
