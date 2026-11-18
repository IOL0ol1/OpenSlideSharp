using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using OpenSlideSharp.Viewer.Controls;
using OpenSlideSharp.Viewer.Core;
using Xunit;

namespace OpenSlideSharp.Viewer.Tests;

public class MainWindowTests
{
    private const string AppTitle = "OpenSlide Viewer";

    [Fact]
    public void Open_ShowsPropertiesSorted()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            host.Open("small.svs");

            string[] keys = host.Window.PropertyRows.Select(row => row.Key).ToArray();
            Assert.Contains(OpenSlideProperties.Vendor, keys);
            Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
            OpenSlide slide = host.Window.Viewer.Slide!;
            Assert.Equal(slide.Properties.Count, keys.Length);
            Assert.All(host.Window.PropertyRows, row => Assert.Equal(slide.Properties[row.Key], row.Value));
            Assert.Same(host.Window.PropertyRows, host.Window.PropertyList.ItemsSource);
        });
    }

    [Fact]
    public void PropertyRows_AreSortedByKey()
    {
        var properties = new Dictionary<string, string>
        {
            ["openslide.vendor"] = "aperio",
            ["b"] = "2",
            ["aperio.MPP"] = "0.499",
            ["a"] = "1",
            ["A"] = "0",
        };

        PropertyRow[] rows = MainWindow.PropertyRowsOf(properties);

        Assert.Equal(["A", "a", "aperio.MPP", "b", "openslide.vendor"], rows.Select(row => row.Key));
        Assert.Equal(["0", "1", "0.499", "2", "aperio"], rows.Select(row => row.Value));
    }

    [Fact]
    public void Properties_CopiesTheSelectedRows()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            host.Open("small.svs");
            ListBox list = host.Window.PropertyList;
            PropertyRow first = host.Window.PropertyRows[0];
            PropertyRow vendor = host.Window.PropertyRows.Single(row => row.Key == OpenSlideProperties.Vendor);
            list.SelectedItems!.Add(vendor);
            list.SelectedItems.Add(first);

            Task copy = host.Window.CopyPropertiesAsync(valuesOnly: false);
            HeadlessSession.PumpUntil(() => copy.IsCompleted, "the rows are copied");
            copy.GetAwaiter().GetResult();

            // In list order, one "key<TAB>value" line each.
            string expected = $"{first.Key}\t{first.Value}{Environment.NewLine}{vendor.Key}\t{vendor.Value}";
            Assert.Equal(expected, ClipboardText(host.Window));

            copy = host.Window.CopyPropertiesAsync(valuesOnly: true);
            HeadlessSession.PumpUntil(() => copy.IsCompleted, "the values are copied");
            Assert.Equal($"{first.Value}{Environment.NewLine}{vendor.Value}", ClipboardText(host.Window));

            // The platform's copy key does the first, from a row with the focus.
            HeadlessSession.Pump();
            Assert.True(((Control)list.ContainerFromItem(first)!).Focus());
            Assert.True(list.IsKeyboardFocusWithin);
            host.Window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            host.Window.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control);
            HeadlessSession.PumpUntil(() => ClipboardText(host.Window) == expected, "Ctrl+C copies the rows");
        });

        static string? ClipboardText(TopLevel topLevel)
        {
            Task<string?> text = topLevel.Clipboard!.TryGetTextAsync();
            HeadlessSession.PumpUntil(() => text.IsCompleted, "the clipboard is read");
            return text.GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void Open_ListsAssociatedImages()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            host.Open("small.svs");
            AssociatedImageRow row = Assert.Single(host.Window.AssociatedImageRows, r => r.Name == "thumbnail");
            Assert.Equal("16 × 16", row.Size);

            // A double click on the list item opens its window.
            host.Window.Tabs.SelectedItem = host.Window.ImagesTab;
            HeadlessSession.Pump();
            var item = (Control)host.Window.ImageList.ContainerFromItem(row)!;
            Point at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), host.Window)!.Value;
            host.Window.MouseDown(at, MouseButton.Left);
            host.Window.MouseUp(at, MouseButton.Left);
            host.Window.MouseDown(at, MouseButton.Left);
            host.Window.MouseUp(at, MouseButton.Left);
            HeadlessSession.PumpUntil(() => host.Window.ImageWindows.Count > 0, "the image window opens");

            AssociatedImageWindow window = Assert.Single(host.Window.ImageWindows);
            Assert.Equal("thumbnail", window.Title);
            Assert.Equal(new PixelSize(16, 16), window.Image!.PixelSize);
            RenderedFrame frame = RenderedFrame.Capture(window);
            Assert.True(frame.Count(c => !RenderedFrame.IsWhite(c)) > 100, "the image is not drawn");

            // The window goes when the slide does.
            host.Open("boxes.tiff");
            Assert.Empty(host.Window.ImageWindows);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void AssociatedImage_IsReadOffTheUiThread_AndOpenedOnce()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            host.Open("small.svs");
            var readThreads = new ConcurrentQueue<int>();
            host.Window.ReadAssociatedImage = image =>
            {
                readThreads.Enqueue(Environment.CurrentManagedThreadId);
                return image.Read();
            };

            // Asked twice before the first read is done (a double click, then Enter), then once more.
            Task<AssociatedImageWindow?> first = host.Window.ShowAssociatedImageAsync("thumbnail");
            Task<AssociatedImageWindow?> second = host.Window.ShowAssociatedImageAsync("thumbnail");
            HeadlessSession.PumpUntil(() => first.IsCompleted && second.IsCompleted, "the image is shown");
            Task<AssociatedImageWindow?> third = host.Window.ShowAssociatedImageAsync("thumbnail");
            HeadlessSession.PumpUntil(() => third.IsCompleted, "the image is shown again");

            AssociatedImageWindow window = Assert.Single(host.Window.ImageWindows);
            Assert.Same(window, first.Result);
            Assert.Same(window, second.Result);
            Assert.Same(window, third.Result);
            Assert.True(window.IsVisible);
            Assert.NotEmpty(readThreads);
            Assert.DoesNotContain(Environment.CurrentManagedThreadId, readThreads);
            Assert.True(readThreads.Count <= 2, $"read {readThreads.Count} times"); // not again once it is shown
        });
    }

    [Fact]
    public void Navigator_ClickCentersView()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            host.Open("boxes.tiff");
            Navigator navigator = host.Window.Navigator;
            Rect image = navigator.ImageRect; // where the slide is drawn, in the navigator
            Assert.True(image.Contains(new Point(10, 10)) && image.Contains(new Point(60, 50)));
            double ratioX = 300 / image.Width; // level-0 pixels per navigator pixel
            double ratioY = 250 / image.Height;
            Viewport viewport = host.Window.Viewer.Viewport!;

            host.Click(navigator, new Point(10, 10));

            Assert.InRange(viewport.CenterX, ((10 - image.X) * ratioX) - ratioX, ((10 - image.X) * ratioX) + ratioX);
            Assert.InRange(viewport.CenterY, ((10 - image.Y) * ratioY) - ratioY, ((10 - image.Y) * ratioY) + ratioY);

            // Dragging follows the pointer.
            Point from = navigator.TranslatePoint(new Point(20, 20), host.Window)!.Value;
            Point to = navigator.TranslatePoint(new Point(60, 50), host.Window)!.Value;
            host.Window.MouseDown(from, MouseButton.Left);
            host.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            Assert.InRange(viewport.CenterX, ((60 - image.X) * ratioX) - ratioX, ((60 - image.X) * ratioX) + ratioX);
            Assert.InRange(viewport.CenterY, ((50 - image.Y) * ratioY) - ratioY, ((50 - image.Y) * ratioY) + ratioY);
            host.Window.MouseUp(to, MouseButton.Left);
        });
    }

    [Fact]
    public void Navigator_DrawsTheThumbnailAndTheView()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            host.Open("boxes.tiff");
            Navigator navigator = host.Window.Navigator;
            Assert.NotNull(navigator.Thumbnail);
            Assert.Same(navigator.Thumbnail, host.Window.Viewer.Thumbnail); // the view's picture for huge levels
            Rect image = navigator.ImageRect;
            Assert.Equal(300.0 / 250, image.Width / image.Height, 2);
            Viewport viewport = host.Window.Viewer.Viewport!;
            viewport.ZoomTo(100, 80, 100, 80);

            RenderedFrame frame = host.Capture();

            // The thumbnail: boxes.tiff's red box is in it.
            Point topLeft = navigator.TranslatePoint(image.TopLeft, host.Window)!.Value;
            int red = 0;
            for (int y = (int)topLeft.Y; y < (int)(topLeft.Y + image.Height); y++)
            {
                for (int x = (int)topLeft.X; x < (int)(topLeft.X + image.Width); x++)
                {
                    red += RenderedFrame.IsRed(frame[x, y]) ? 1 : 0;
                }
            }

            Assert.True(red > 100, $"{red} red pixels in the thumbnail");

            // The view: a rectangle round its level-0 bounds, scaled like the thumbnail.
            (double left0, double top0) = viewport.ScreenToSlide(0, 0);
            (double right0, double bottom0) = viewport.ScreenToSlide(viewport.ViewWidth, viewport.ViewHeight);
            double scale = image.Width / 300;
            double left = image.X + (left0 * scale), right = image.X + (right0 * scale);
            double top = image.Y + (top0 * scale), bottom = image.Y + (bottom0 * scale);
            Assert.True(right - left < image.Width * 0.75, "the view is not zoomed in");
            foreach (Point edge in new[] { new Point(left, (top + bottom) / 2), new Point(right, (top + bottom) / 2), new Point((left + right) / 2, top), new Point((left + right) / 2, bottom) })
            {
                Point at = navigator.TranslatePoint(edge, host.Window)!.Value;
                Assert.True(Near(frame, at, Navigator.ViewColor), $"no view rectangle at {edge} in the navigator");
            }
        });

        static bool Near(RenderedFrame frame, Point at, Color color)
        {
            for (int y = (int)at.Y - 2; y <= (int)at.Y + 2; y++)
            {
                for (int x = (int)at.X - 2; x <= (int)at.X + 2; x++)
                {
                    (byte r, byte g, byte b) = frame[x, y];
                    if (Math.Abs(r - color.R) <= 40 && Math.Abs(g - color.G) <= 40 && Math.Abs(b - color.B) <= 40)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    [Fact]
    public void Selections_ZoomTo()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            host.Open("boxes.tiff");
            var selection = new Selection(20, 30, 240, 150);
            host.Window.Viewer.Selections.Add(selection);
            host.Window.Tabs.SelectedItem = host.Window.SelectionsTab;
            HeadlessSession.Pump();
            Assert.Single(host.Window.SelectionRows);

            host.ClickCenter(FindButton(host.Window.SelectionsList, "Zoom to"));

            Viewport viewport = host.Window.Viewer.Viewport!;
            (double left, double top) = viewport.SlideToScreen(selection.X0, selection.Y0);
            (double right, double bottom) = viewport.SlideToScreen(selection.X0 + selection.Width0, selection.Y0 + selection.Height0);
            Assert.True(left >= -1 && top >= -1, $"({left}, {top}) is outside the view");
            Assert.True(right <= viewport.ViewWidth + 1 && bottom <= viewport.ViewHeight + 1, $"({right}, {bottom}) is outside the view");
            Assert.True(
                Math.Abs(right - left - viewport.ViewWidth) <= 1 || Math.Abs(bottom - top - viewport.ViewHeight) <= 1,
                $"{right - left} x {bottom - top} in a {viewport.ViewWidth} x {viewport.ViewHeight} view touches no side");

            host.ClickCenter(FindButton(host.Window.SelectionsList, "Delete"));
            Assert.Empty(host.Window.Viewer.Selections.Items);
            Assert.Empty(host.Window.SelectionRows);
        });
    }

    [Fact]
    public void Open_UnopenableFile_ShowsErrorAndKeepsRunning()
    {
        string text = Path.Combine(Path.GetTempPath(), $"not-a-slide-{Guid.NewGuid():N}.txt");
        File.WriteAllText(text, "This is not a slide.");
        try
        {
            HeadlessSession.Run(() =>
            {
                using var host = new WindowHost();
                host.Open("boxes.tiff");
                host.WaitForTiles();
                OpenSlide boxes = host.Window.Viewer.Slide!;
                PropertyRow[] properties = host.Window.PropertyRows.ToArray();
                Assert.False(host.Window.ErrorText.IsVisible);

                host.Open("unopenable.tiff");

                Assert.True(host.Window.ErrorText.IsVisible);
                Assert.Contains("unopenable.tiff", host.Window.ErrorText.Text);
                Assert.Same(boxes, host.Window.Viewer.Slide);
                Assert.Equal(new SlideSize(300, 250), boxes.Dimensions); // not disposed
                Assert.Equal(properties, host.Window.PropertyRows);
                Assert.Equal($"{AppTitle} — boxes.tiff", host.Window.Title);
                Assert.True(host.Capture().Count(RenderedFrame.IsRed) > 100, "the boxes are no longer drawn");

                // A text file, and a file that does not exist.
                host.OpenPath(text);
                Assert.Contains(Path.GetFileName(text), host.Window.ErrorText.Text);
                host.OpenPath(Path.Combine(Path.GetTempPath(), "missing.svs"));
                Assert.Contains("missing.svs", host.Window.ErrorText.Text);
                Assert.Same(boxes, host.Window.Viewer.Slide);

                // A slide that opens clears the error.
                host.Open("small.svs");
                Assert.False(host.Window.ErrorText.IsVisible);
            });
        }
        finally
        {
            File.Delete(text);
        }
    }

    [Fact]
    public void Open_UnexpectedFailure_ShowsErrorAndKeepsRunning()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            host.Open("boxes.tiff");
            OpenSlide boxes = host.Window.Viewer.Slide!;
            // Not one of the failures OpenSlide reports for a bad file: a broken native library, say.
            host.Window.OpenSlideFile = _ => throw new EntryPointNotFoundException("openslide_open is missing.");

            host.Open("small.svs");

            Assert.True(host.Window.ErrorText.IsVisible);
            Assert.Equal("Cannot open small.svs: openslide_open is missing.", host.Window.ErrorText.Text);
            Assert.Same(boxes, host.Window.Viewer.Slide);
            Assert.Equal($"{AppTitle} — boxes.tiff", host.Window.Title);

            // The same from a drop, whose handler nothing awaits.
            host.Window.ErrorText.Text = null;
            using var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(host.File("small.svs")));
            foreach (RawDragEventType type in new[] { RawDragEventType.DragEnter, RawDragEventType.DragOver, RawDragEventType.Drop })
            {
                host.Window.DragDrop(new Point(600, 300), type, data, DragDropEffects.Copy);
            }

            HeadlessSession.PumpUntil(() => host.Window.ErrorText.Text is not null, "the failure is shown");
            Assert.Equal("Cannot open small.svs: openslide_open is missing.", host.Window.ErrorText.Text);

            host.Window.OpenSlideFile = OpenSlide.Open;
            host.Open("small.svs");
            Assert.False(host.Window.ErrorText.IsVisible);
            Assert.Equal($"{AppTitle} — small.svs", host.Window.Title);
        });
    }

    [Fact]
    public void Properties_ClipboardFailure_ShowsErrorAndKeepsRunning()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            host.Open("small.svs");
            host.Window.SetClipboardText = _ => throw new InvalidOperationException("The clipboard is busy.");
            ListBox list = host.Window.PropertyList;
            PropertyRow first = host.Window.PropertyRows[0];
            list.SelectedItems!.Add(first);
            HeadlessSession.Pump();
            Assert.True(((Control)list.ContainerFromItem(first)!).Focus());

            // Ctrl+C runs an event handler that nothing awaits.
            host.Window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            host.Window.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control);
            HeadlessSession.PumpUntil(() => host.Window.ErrorText.IsVisible, "the failure is shown");

            Assert.Equal("Cannot copy the properties: The clipboard is busy.", host.Window.ErrorText.Text);
            host.Open("boxes.tiff"); // still running
            Assert.Equal($"{AppTitle} — boxes.tiff", host.Window.Title);
        });
    }

    [Fact]
    public void Open_ShowsOpeningInTheStatusBarUntilDone()
    {
        using var gate = new ManualResetEventSlim();
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            host.Window.OpenSlideFile = path =>
            {
                gate.Wait(TimeSpan.FromSeconds(30));
                return OpenSlide.Open(path);
            };
            try
            {
                Task open = host.Window.OpenAsync(TestFiles.Get("boxes.tiff"));
                HeadlessSession.Pump();

                Assert.True(host.Window.ActivityText.IsVisible);
                Assert.Equal("Opening boxes.tiff…", host.Window.ActivityText.Text);

                gate.Set();
                HeadlessSession.PumpUntil(() => open.IsCompleted, "boxes.tiff is opened");
                Assert.False(host.Window.ActivityText.IsVisible);
                Assert.Equal($"{AppTitle} — boxes.tiff", host.Window.Title);

                // Until the error, for a file that cannot be opened.
                gate.Reset();
                open = host.Window.OpenAsync(TestFiles.Get("unopenable.tiff"));
                HeadlessSession.Pump();
                Assert.Equal("Opening unopenable.tiff…", host.Window.ActivityText.Text);
                gate.Set();
                HeadlessSession.PumpUntil(() => open.IsCompleted, "unopenable.tiff is tried");
                Assert.False(host.Window.ActivityText.IsVisible);
                Assert.Contains("unopenable.tiff", host.Window.ErrorText.Text);

                // File > Close while opening: the open is dropped, and so is its message.
                gate.Reset();
                open = host.Window.OpenAsync(TestFiles.Get("small.svs"));
                HeadlessSession.Pump();
                host.Window.CloseSlide();
                Assert.False(host.Window.ActivityText.IsVisible);
                gate.Set();
                HeadlessSession.PumpUntil(() => open.IsCompleted, "small.svs is opened and dropped");
                Assert.False(host.Window.ActivityText.IsVisible);
                Assert.Null(host.Window.Viewer.Slide);
            }
            finally
            {
                gate.Set();
            }
        });
    }

    [Fact]
    public void SwitchSlide_DoesNotShowOldTiles()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            host.Open("boxes.tiff");
            host.WaitForTiles();
            OpenSlide boxes = host.Window.Viewer.Slide!;
            Assert.True(host.Capture().Count(RenderedFrame.IsRed) > 100, "the boxes are not drawn");

            host.Open("small.svs");
            host.WaitForTiles();

            int red = host.Capture().Count(RenderedFrame.IsRed);
            Assert.True(red < 10, $"{red} red pixels after switching to small.svs");
            Assert.Throws<ObjectDisposedException>(() => boxes.Dimensions); // the old slide is closed
        });
    }

    [Fact]
    public void Title_ShowsFileName()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            Assert.Equal(AppTitle, host.Window.Title);

            host.Open("boxes.tiff");
            Assert.Equal($"{AppTitle} — boxes.tiff", host.Window.Title);

            host.Open("small.svs");
            Assert.Equal($"{AppTitle} — small.svs", host.Window.Title);

            host.Window.CloseSlide();
            Assert.Equal(AppTitle, host.Window.Title);
        });
    }

    [Fact]
    public void CloseSlide_ClearsTheWindow()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            host.Open("small.svs");
            host.WaitForTiles();
            OpenSlide slide = host.Window.Viewer.Slide!;
            host.Window.Viewer.Selections.Add(new Selection(1, 1, 4, 4));

            host.Window.CloseSlide();

            Assert.Null(host.Window.Viewer.Slide);
            Assert.Throws<ObjectDisposedException>(() => slide.Dimensions);
            Assert.Empty(host.Window.PropertyRows);
            Assert.Empty(host.Window.AssociatedImageRows);
            Assert.Empty(host.Window.SelectionRows);
            Assert.Null(host.Window.Navigator.Thumbnail);
            Assert.Null(host.Window.Viewer.Thumbnail);
            Assert.Equal("", host.Window.ZoomText.Text ?? "");
            host.Capture(); // draws the empty window
        });
    }

    [Fact]
    public void Close_DisposesTheSlide()
    {
        OpenSlide slide = HeadlessSession.Run(() =>
        {
            var host = new WindowHost();
            host.Open("boxes.tiff");
            host.WaitForTiles();
            OpenSlide opened = host.Window.Viewer.Slide!;
            host.Dispose();
            Assert.Null(host.Window.Viewer.Loader);
            return opened;
        });

        Assert.Throws<ObjectDisposedException>(() => slide.Dimensions);
    }

    [Fact]
    public void Open_TwiceQuickly_ShowsTheLastSlide()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            Task first = host.Window.OpenAsync(TestFiles.Get("boxes.tiff"));
            Task second = host.Window.OpenAsync(TestFiles.Get("small.svs"));
            HeadlessSession.PumpUntil(() => first.IsCompleted && second.IsCompleted, "both slides open");

            Assert.Equal($"{AppTitle} — small.svs", host.Window.Title);
            Assert.Equal(new SlideSize(16, 16), host.Window.Viewer.Slide!.Dimensions);
            Assert.False(host.Window.ErrorText.IsVisible);
        });
    }

    [Fact]
    public void Drop_OpensTheFirstFile()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            using var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(host.File("small.svs")));
            data.Add(DataTransferItem.CreateFile(host.File("boxes.tiff")));
            var at = new Point(600, 300);

            host.Window.DragDrop(at, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
            host.Window.DragDrop(at, RawDragEventType.DragOver, data, DragDropEffects.Copy);
            host.Window.DragDrop(at, RawDragEventType.Drop, data, DragDropEffects.Copy);
            HeadlessSession.PumpUntil(() => host.Window.Viewer.Slide is not null, "the dropped slide opens");

            Assert.Equal($"{AppTitle} — small.svs", host.Window.Title);
        });
    }

    [Fact]
    public void StatusBar_ShowsPointerZoomAndSizes()
    {
        HeadlessSession.Run(() =>
        {
            using var host = new WindowHost();
            host.Open("boxes.tiff"); // 72 dpi (352.8 µm a pixel), no objective power
            SlideView view = host.Window.Viewer;
            Viewport viewport = view.Viewport!;
            OpenSlide boxes = view.Slide!;
            double? mppX = StatusFormatter.Number(boxes.Properties, OpenSlideProperties.MppX);
            double? mppY = StatusFormatter.Number(boxes.Properties, OpenSlideProperties.MppY);

            Point center = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), host.Window)!.Value;
            host.Window.MouseMove(center);
            (double X, double Y) position = view.PointerSlidePosition!.Value;
            Assert.Equal(StatusFormatter.Coordinates(position.X, position.Y), host.Window.CoordinatesText.Text);
            Assert.Equal(StatusFormatter.Zoom(viewport.Downsample, null), host.Window.ZoomText.Text);
            Assert.DoesNotContain("×", host.Window.ZoomText.Text); // no magnification without an objective power
            string viewSize = StatusFormatter.PhysicalSize(
                viewport.ViewWidth * viewport.Downsample, viewport.ViewHeight * viewport.Downsample, mppX, mppY);
            Assert.EndsWith(" mm", viewSize);
            Assert.Equal("View: " + viewSize, host.Window.ViewSizeText.Text);
            Assert.False(host.Window.SelectionSizeText.IsVisible); // no selection yet

            // Off the slide (above it, in the fitted view), no coordinates.
            (double _, double top) = viewport.SlideToScreen(0, 0);
            Assert.True(top > 2, "the slide fills the view's height");
            host.Window.MouseMove(view.TranslatePoint(new Point(view.Bounds.Width / 2, top / 2), host.Window)!.Value);
            Assert.Equal("", host.Window.CoordinatesText.Text ?? "");

            // A slide with an objective power shows the magnification; selections show their size.
            host.Open("small.svs");
            OpenSlide small = view.Slide!;
            mppX = StatusFormatter.Number(small.Properties, OpenSlideProperties.MppX);
            mppY = StatusFormatter.Number(small.Properties, OpenSlideProperties.MppY);
            double? objective = StatusFormatter.Number(small.Properties, OpenSlideProperties.ObjectivePower);
            Assert.Equal(20, objective);
            viewport = view.Viewport!;
            Assert.Equal(StatusFormatter.Zoom(viewport.Downsample, objective), host.Window.ZoomText.Text);
            Assert.Contains("×)", host.Window.ZoomText.Text);
            viewSize = StatusFormatter.PhysicalSize(
                viewport.ViewWidth * viewport.Downsample, viewport.ViewHeight * viewport.Downsample, mppX, mppY);
            Assert.Equal("View: " + viewSize, host.Window.ViewSizeText.Text);

            view.Selections.Add(new Selection(2, 2, 8, 4));
            Assert.Equal("Selection: " + StatusFormatter.PhysicalSize(8, 4, mppX, mppY), host.Window.SelectionSizeText.Text);
            Assert.True(host.Window.SelectionSizeText.IsVisible);

            // Zooming updates the zoom and the size of the view.
            viewport.ZoomAt(viewport.ViewWidth / 2, viewport.ViewHeight / 2, 2);
            Assert.Equal(StatusFormatter.Zoom(viewport.Downsample, objective), host.Window.ZoomText.Text);
            Assert.Equal(
                "View: " + StatusFormatter.PhysicalSize(viewport.ViewWidth * viewport.Downsample, viewport.ViewHeight * viewport.Downsample, mppX, mppY),
                host.Window.ViewSizeText.Text);
        });
    }

    private static Button FindButton(Control within, string content) =>
        within.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, content));

    /// <summary>A 1000 x 700 <see cref="MainWindow"/>; disposing closes it.</summary>
    private sealed class WindowHost : IDisposable
    {
        public WindowHost()
        {
            Window = new MainWindow { Width = 1000, Height = 700 };
            Window.Show();
            HeadlessSession.Pump();
        }

        public MainWindow Window { get; }

        /// <summary>Opens a fixture and waits until the window shows it (or the error).</summary>
        public void Open(string fixture) => OpenPath(TestFiles.Get(fixture));

        public void OpenPath(string path)
        {
            Task open = Window.OpenAsync(path);
            HeadlessSession.PumpUntil(() => open.IsCompleted, Path.GetFileName(path) + " is opened");
            open.GetAwaiter().GetResult();
            HeadlessSession.Pump(); // lays the window out again
        }

        /// <summary>Renders until the view has read its tiles and has none left to read.</summary>
        public void WaitForTiles() =>
            HeadlessSession.PumpUntil(
                () => Window.Viewer.TileReads > 0 && Window.Viewer.PendingTiles == 0, "the visible tiles are loaded");

        public RenderedFrame Capture() => RenderedFrame.Capture(Window);

        /// <summary>A fixture as the storage provider hands it over, as drag and drop carries files.</summary>
        public IStorageFile File(string fixture)
        {
            Task<IStorageFile?> file = Window.StorageProvider.TryGetFileFromPathAsync(TestFiles.Get(fixture));
            HeadlessSession.PumpUntil(() => file.IsCompleted, fixture + " is found");
            return file.GetAwaiter().GetResult() ?? throw new FileNotFoundException(null, fixture);
        }

        /// <summary>Clicks <paramref name="control"/> with the left button at <paramref name="at"/>, in its coordinates.</summary>
        public void Click(Control control, Point at)
        {
            Point point = control.TranslatePoint(at, Window)!.Value;
            Window.MouseDown(point, MouseButton.Left);
            Window.MouseUp(point, MouseButton.Left);
        }

        public void ClickCenter(Control control) =>
            Click(control, new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));

        public void Dispose()
        {
            Window.Close();
            HeadlessSession.Pump();
        }
    }
}
