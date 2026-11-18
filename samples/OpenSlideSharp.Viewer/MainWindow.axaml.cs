using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using OpenSlideSharp.Viewer.Controls;
using OpenSlideSharp.Viewer.Core;

namespace OpenSlideSharp.Viewer;

/// <summary>
/// The viewer's window: the slide in a <see cref="SlideView"/>, with a navigator, the slide's properties,
/// associated images and selections on the left, and the pointer position, zoom and sizes in the status bar.
/// </summary>
/// <remarks>
/// A slide is opened from the command line (see <see cref="App"/>), File → Open, or by dropping a file on the
/// window. A file that cannot be opened is reported in the status bar and the slide shown stays. Opening another
/// slide, File → Close and closing the window close the slide shown, after the view has stopped reading it.
/// </remarks>
internal sealed partial class MainWindow : Window
{
    private const string AppTitle = "OpenSlide Viewer";
    private const string ThumbnailImageName = "thumbnail";
    private const int ThumbnailSize = 256;

    // GetThumbnail reads a whole level into memory; above this many pixels (64 MiB, as on a slide with a single
    // large level) the navigator uses the slide's thumbnail image instead.
    private const double MaxThumbnailSourcePixels = 16 * 1024 * 1024;

    // The formats OpenSlide reads (https://openslide.org/formats/).
    private static readonly FilePickerFileType SlideFiles = new("Slides")
    {
        Patterns =
        [
            "*.svs", "*.tif", "*.tiff", "*.ndpi", "*.vms", "*.vmu", "*.scn", "*.mrxs", "*.svslide", "*.bif", "*.dcm",
            "*.czi",
        ],
    };

    private readonly List<AssociatedImageWindow> _imageWindows = new();
    private PropertyRow[] _propertyRows = [];
    private AssociatedImageRow[] _imageRows = [];
    private SelectionRow[] _selectionRows = [];
    private OpenSlide? _slide;
    private SlideSize _slideSize;
    private Bitmap? _thumbnail;
    private double? _mppX;
    private double? _mppY;
    private double? _objectivePower;
    private int _openRequest; // counts opens (and closes): an open that finishes after a newer one is dropped
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        Viewer.ViewChanged += UpdateStatus;
        Viewer.LoadFailed += OnLoadFailed;
        Viewer.Selections.Changed += OnSelectionsChanged;
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        UpdateStatus();
    }

    internal IReadOnlyList<PropertyRow> PropertyRows => _propertyRows;

    internal IReadOnlyList<AssociatedImageRow> AssociatedImageRows => _imageRows;

    internal IReadOnlyList<SelectionRow> SelectionRows => _selectionRows;

    internal IReadOnlyList<AssociatedImageWindow> ImageWindows => _imageWindows;

    /// <summary>
    /// Opens the slide at <paramref name="path"/> (on a background thread) and shows it instead of the current
    /// one. If it cannot be opened, the status bar says why and the current slide stays. An open overtaken by a
    /// newer one, or by File → Close, is dropped when it finishes.
    /// </summary>
    public async Task OpenAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int request = ++_openRequest;
        LoadedSlide loaded;
        try
        {
            loaded = await Task.Run(() => LoadedSlide.Load(path));
        }
        catch (Exception e) when (e is OpenSlideException or IOException or UnauthorizedAccessException
            or ArgumentException or DllNotFoundException)
        {
            if (request == _openRequest && !_closed)
            {
                ShowError($"Cannot open {Path.GetFileName(path)}: {e.Message}");
            }

            return;
        }

        if (request != _openRequest || _closed)
        {
            loaded.Dispose();
            return;
        }

        Show(loaded);
    }

    /// <summary>Closes the slide shown (File → Close).</summary>
    internal void CloseSlide()
    {
        _openRequest++; // an open still running is dropped
        Viewer.Slide = null; // the view stops reading the slide before it is closed
        _slide?.Dispose();
        _slide = null;
        _slideSize = default;
        CloseImageWindows();
        Navigator.Show(default, null, null);
        _thumbnail?.Dispose();
        _thumbnail = null;
        _propertyRows = [];
        PropertyList.ItemsSource = _propertyRows;
        _imageRows = [];
        ImageList.ItemsSource = _imageRows;
        (_mppX, _mppY, _objectivePower) = (null, null, null);
        Title = AppTitle;
        CloseMenuItem.IsEnabled = false;
        ViewMenu.IsEnabled = false;
        DropHint.IsVisible = true;
        ClearError();
        UpdateStatus();
    }

    /// <summary>
    /// Opens a window showing the associated image <paramref name="name"/> of the slide; null if there is no such
    /// image or it cannot be read (the status bar then says why).
    /// </summary>
    internal AssociatedImageWindow? ShowAssociatedImage(string name)
    {
        if (_slide is not { } slide || !slide.AssociatedImages.TryGetValue(name, out AssociatedImage? image))
        {
            return null;
        }

        PixelImage pixels;
        try
        {
            pixels = image.Read();
        }
        catch (Exception e) when (e is OpenSlideException or ArgumentOutOfRangeException)
        {
            ShowError($"Cannot read the {name} image: {e.Message}");
            return null;
        }

        var window = new AssociatedImageWindow(name, pixels);
        window.Closed += (_, _) => _imageWindows.Remove(window);
        _imageWindows.Add(window);
        window.Show(this);
        return window;
    }

    /// <summary>Copies the selected properties, in list order, one per line: <c>key&lt;TAB&gt;value</c>, or just the values.</summary>
    internal async Task CopyPropertiesAsync(bool valuesOnly)
    {
        var selected = new HashSet<PropertyRow>(PropertyList.SelectedItems?.OfType<PropertyRow>() ?? []);
        string text = string.Join(
            Environment.NewLine,
            _propertyRows.Where(selected.Contains).Select(row => valuesOnly ? row.Value : row.Key + "\t" + row.Value));
        if (text.Length > 0 && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    /// <summary>Returns the rows of the Properties list: one per property, sorted by key (ordinal, as OpenSlide does).</summary>
    internal static PropertyRow[] PropertyRowsOf(IEnumerable<KeyValuePair<string, string>> properties) =>
        properties
            .OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => new PropertyRow(property.Key, property.Value))
            .ToArray();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        CloseSlide();
        base.OnClosed(e);
    }

    // The navigator's picture, read on the thread that opens the slide: GetThumbnail if the level it reads is not
    // too large, else (or if that fails) the slide's thumbnail image.
    private static PixelImage? ReadThumbnail(OpenSlide slide)
    {
        SlideSize size = slide.Dimensions;
        double downsample = Math.Max((double)size.Width / ThumbnailSize, (double)size.Height / ThumbnailSize);
        SlideSize level = slide.LevelDimensions[slide.GetBestLevelForDownsample(downsample)];
        if ((double)level.Width * level.Height <= MaxThumbnailSourcePixels)
        {
            try
            {
                return slide.GetThumbnail(ThumbnailSize, ThumbnailSize);
            }
            catch (OpenSlideException e)
            {
                Trace.TraceWarning($"Cannot make a thumbnail of {slide.FileName}: {e.Message}");
            }
        }

        if (slide.AssociatedImages.TryGetValue(ThumbnailImageName, out AssociatedImage? image)
            && (double)image.Dimensions.Width * image.Dimensions.Height <= MaxThumbnailSourcePixels)
        {
            try
            {
                return image.Read();
            }
            catch (OpenSlideException e)
            {
                Trace.TraceWarning($"Cannot read the thumbnail image of {slide.FileName}: {e.Message}");
            }
        }

        return null; // the navigator still works, without a picture
    }

    private void Show(LoadedSlide loaded)
    {
        try
        {
            // First, so that the view stops reading the old slide before it is closed.
            Viewer.Slide = loaded.Slide;
        }
        catch (OpenSlideException e)
        {
            ShowError($"Cannot open {Path.GetFileName(loaded.Path)}: {e.Message}");
            loaded.Dispose();
            return;
        }

        _slide?.Dispose();
        _slide = loaded.Slide;
        _slideSize = loaded.Size;
        CloseImageWindows();
        Bitmap? oldThumbnail = _thumbnail;
        _thumbnail = loaded.Thumbnail?.ToBitmap();
        Navigator.Show(loaded.Size, _thumbnail, Viewer.Viewport);
        Viewer.Thumbnail = _thumbnail; // drawn under the tiles of a slide too large to draw from tiles when zoomed out
        oldThumbnail?.Dispose(); // a frame drawn with it keeps its own reference
        _propertyRows = loaded.Properties;
        PropertyList.ItemsSource = _propertyRows;
        _imageRows = loaded.Images;
        ImageList.ItemsSource = _imageRows;
        (_mppX, _mppY, _objectivePower) = (loaded.MppX, loaded.MppY, loaded.ObjectivePower);
        Title = $"{AppTitle} — {Path.GetFileName(loaded.Path)}";
        CloseMenuItem.IsEnabled = true;
        ViewMenu.IsEnabled = true;
        DropHint.IsVisible = false;
        ClearError();
        OnSelectionsChanged(); // the view cleared them; the rows show the new slide's sizes
        Viewer.Focus();
    }

    private void CloseImageWindows()
    {
        foreach (AssociatedImageWindow window in _imageWindows.ToArray())
        {
            window.Close();
        }

        _imageWindows.Clear();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ToolTip.SetTip(ErrorText, message);
        ErrorText.IsVisible = true;
    }

    private void ClearError()
    {
        ErrorText.Text = null;
        ToolTip.SetTip(ErrorText, null);
        ErrorText.IsVisible = false;
    }

    private void OnLoadFailed(Exception error) =>
        ShowError($"Cannot read {Path.GetFileName(_slide?.FileName)}: {error.Message}");

    private void UpdateStatus()
    {
        Viewport? viewport = Viewer.Viewport;
        CoordinatesText.Text = Viewer.PointerSlidePosition is { } pointer
            && pointer.X >= 0 && pointer.Y >= 0 && pointer.X < _slideSize.Width && pointer.Y < _slideSize.Height
                ? StatusFormatter.Coordinates(pointer.X, pointer.Y)
                : ""; // off the view or off the slide
        ZoomText.Text = viewport is null ? "" : StatusFormatter.Zoom(viewport.Downsample, _objectivePower);
        string view = viewport is null
            ? ""
            : StatusFormatter.PhysicalSize(
                viewport.ViewWidth * viewport.Downsample, viewport.ViewHeight * viewport.Downsample, _mppX, _mppY);
        SetSegment(ViewSizeText, view.Length > 0 ? "View: " + view : "");

        // The selection picked in the list, else the latest one.
        Selection? selection = SelectionsList.SelectedItem is SelectionRow row
            ? row.Selection
            : Viewer.Selections.Items.LastOrDefault();
        string selected = selection is null
            ? ""
            : StatusFormatter.PhysicalSize(selection.Width0, selection.Height0, _mppX, _mppY);
        SetSegment(SelectionSizeText, selected.Length > 0 ? "Selection: " + selected : "");

        static void SetSegment(TextBlock block, string text)
        {
            block.Text = text;
            block.IsVisible = text.Length > 0;
        }
    }

    private void OnSelectionsChanged()
    {
        IReadOnlyList<Selection> selections = Viewer.Selections.Items;
        _selectionRows = selections.Select((selection, index) => new SelectionRow(index, selection, Describe(index, selection))).ToArray();
        SelectionsList.ItemsSource = _selectionRows;
        UpdateStatus();
    }

    private string Describe(int index, Selection selection)
    {
        string text = string.Create(
            CultureInfo.InvariantCulture,
            $"{index + 1}.  {selection.Width0:0} × {selection.Height0:0} px at {selection.X0:0}, {selection.Y0:0}");
        string size = StatusFormatter.PhysicalSize(selection.Width0, selection.Height0, _mppX, _mppY);
        return size.Length > 0 ? $"{text} ({size})" : text;
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open slide",
            AllowMultiple = false,
            FileTypeFilter = [SlideFiles, FilePickerFileTypes.All],
        });
        if (files is [IStorageFile file, ..])
        {
            await OpenStorageItemAsync(file);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => CloseSlide();

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnFitClick(object? sender, RoutedEventArgs e) => Viewer.Viewport?.Fit();

    private void OnOneToOneClick(object? sender, RoutedEventArgs e) => Viewer.Viewport?.OneToOne();

    private void OnCenterClick(object? sender, RoutedEventArgs e) => Viewer.Viewport?.Center();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // Opens the first of the files dropped.
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is [IStorageItem first, ..])
        {
            e.Handled = true;
            await OpenStorageItemAsync(first);
        }
    }

    private Task OpenStorageItemAsync(IStorageItem item)
    {
        if (item.TryGetLocalPath() is { } path)
        {
            return OpenAsync(path);
        }

        ShowError($"Cannot open {item.Name}: it is not a local file.");
        return Task.CompletedTask;
    }

    private async void OnPropertyListKeyDown(object? sender, KeyEventArgs e)
    {
        if (this.GetPlatformSettings()?.HotkeyConfiguration.Copy.Any(gesture => gesture.Matches(e)) == true)
        {
            e.Handled = true;
            await CopyPropertiesAsync(valuesOnly: false);
        }
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e) => await CopyPropertiesAsync(valuesOnly: false);

    private async void OnCopyValueClick(object? sender, RoutedEventArgs e) => await CopyPropertiesAsync(valuesOnly: true);

    private void OnImageListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is AssociatedImageRow row)
        {
            e.Handled = true;
            ShowAssociatedImage(row.Name);
        }
    }

    private void OnImageListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ImageList.SelectedItem is AssociatedImageRow row)
        {
            e.Handled = true;
            ShowAssociatedImage(row.Name);
        }
    }

    private void OnSelectionsListChanged(object? sender, SelectionChangedEventArgs e) => UpdateStatus();

    private void OnZoomToSelectionClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SelectionRow { Selection: var selection } && Viewer.Viewport is { } viewport)
        {
            e.Handled = true;
            viewport.ZoomTo(selection.X0, selection.Y0, selection.Width0, selection.Height0);
        }
    }

    private void OnDeleteSelectionClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SelectionRow row && row.Index < Viewer.Selections.Items.Count)
        {
            e.Handled = true;
            Viewer.Selections.RemoveAt(row.Index);
        }
    }

    /// <summary>A slide opened on a background thread, with what the window shows about it.</summary>
    private sealed class LoadedSlide(
        string path, OpenSlide slide, PropertyRow[] properties, AssociatedImageRow[] images, PixelImage? thumbnail)
        : IDisposable
    {
        public string Path { get; } = path;

        public OpenSlide Slide { get; } = slide;

        public SlideSize Size { get; } = slide.Dimensions;

        /// <summary>Gets the properties, sorted by key.</summary>
        public PropertyRow[] Properties { get; } = properties;

        public AssociatedImageRow[] Images { get; } = images;

        public PixelImage? Thumbnail { get; } = thumbnail;

        public double? MppX { get; } = StatusFormatter.Number(slide.Properties, OpenSlideProperties.MppX);

        public double? MppY { get; } = StatusFormatter.Number(slide.Properties, OpenSlideProperties.MppY);

        public double? ObjectivePower { get; } = StatusFormatter.Number(slide.Properties, OpenSlideProperties.ObjectivePower);

        public static LoadedSlide Load(string path)
        {
            OpenSlide slide = OpenSlide.Open(path);
            try
            {
                PropertyRow[] properties = PropertyRowsOf(slide.Properties);
                AssociatedImageRow[] images = slide.AssociatedImages.Values
                    .OrderBy(image => image.Name, StringComparer.Ordinal)
                    .Select(image => new AssociatedImageRow(
                        image.Name,
                        string.Create(CultureInfo.InvariantCulture, $"{image.Dimensions.Width} × {image.Dimensions.Height}")))
                    .ToArray();
                return new LoadedSlide(path, slide, properties, images, ReadThumbnail(slide));
            }
            catch
            {
                slide.Dispose();
                throw;
            }
        }

        public void Dispose() => Slide.Dispose();
    }
}

/// <summary>A row of the Properties list.</summary>
internal sealed record PropertyRow(string Key, string Value);

/// <summary>A row of the Associated images list: the image's name and its size in pixels.</summary>
internal sealed record AssociatedImageRow(string Name, string Size);

/// <summary>A row of the Selections list: the selection at <paramref name="Index"/> in the view's list.</summary>
internal sealed record SelectionRow(int Index, Selection Selection, string Text);
