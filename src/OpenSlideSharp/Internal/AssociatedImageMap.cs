using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using OpenSlideSharp.Native;

namespace OpenSlideSharp;

/// <summary>
/// A read-only view of a slide's associated images. The names (and the sizes, which never change either) are read
/// once on first access and cached, but every member still checks that the slide is open and not in the error state
/// first, so it throws once the slide is disposed or broken.
/// </summary>
internal sealed class AssociatedImageMap : IReadOnlyDictionary<string, AssociatedImage>
{
    private readonly OpenSlide _slide;
    private readonly object _gate = new object();
    private Dictionary<string, AssociatedImage>? _images;
    private List<AssociatedImage>? _ordered;

    internal AssociatedImageMap(OpenSlide slide) => _slide = slide;

    public AssociatedImage this[string key]
    {
        get
        {
            if (TryGetValue(key, out AssociatedImage? value))
            {
                return value;
            }

            throw new KeyNotFoundException($"The associated image '{key}' was not found.");
        }
    }

    public int Count => Load().Count;

    public IEnumerable<string> Keys
    {
        get
        {
            List<AssociatedImage> list = Load();
            var names = new List<string>(list.Count);
            foreach (AssociatedImage image in list)
            {
                names.Add(image.Name);
            }

            return names;
        }
    }

    public IEnumerable<AssociatedImage> Values => Load().ToArray();

    public bool ContainsKey(string key) => TryGetValue(key, out _);

#if NET5_0_OR_GREATER
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out AssociatedImage value)
#else
    // IReadOnlyDictionary<,>.TryGetValue carries no nullable annotations before .NET 5.
    public bool TryGetValue(string key, out AssociatedImage value)
#endif
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        Load();
        bool found = _images!.TryGetValue(key, out AssociatedImage? image);
        value = image!;
        return found;
    }

    public IEnumerator<KeyValuePair<string, AssociatedImage>> GetEnumerator()
    {
        List<AssociatedImage> list = Load();
        var pairs = new List<KeyValuePair<string, AssociatedImage>>(list.Count);
        foreach (AssociatedImage image in list)
        {
            pairs.Add(new KeyValuePair<string, AssociatedImage>(image.Name, image));
        }

        return pairs.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Checks the slide state on every call, then returns the cached list, reading it on first use.
    private List<AssociatedImage> Load()
    {
        _slide.ThrowIfUnusable();
        List<AssociatedImage>? ordered = Volatile.Read(ref _ordered);
        if (ordered is not null)
        {
            return ordered;
        }

        List<AssociatedImage> read = _slide.Invoke(osr => ReadAll(osr));
        var images = new Dictionary<string, AssociatedImage>(read.Count, StringComparer.Ordinal);
        foreach (AssociatedImage image in read)
        {
            images[image.Name] = image;
        }

        // Concurrent first readers may both read; the first to publish wins so all share one set of instances.
        // _images is written before _ordered, the field that readers check.
        lock (_gate)
        {
            if (_ordered is null)
            {
                _images = images;
                Volatile.Write(ref _ordered, read);
            }

            return _ordered!;
        }
    }

    // The native array and strings belong to the slide; copy them into managed objects before returning.
    private unsafe List<AssociatedImage> ReadAll(IntPtr osr)
    {
        var result = new List<AssociatedImage>();
        byte** p = openslide.get_associated_image_names(osr);
        if (p == null)
        {
            return result;
        }

        for (; *p != null; p++)
        {
            string name = Utf8.FromNative(*p) ?? string.Empty;
            byte[] key = Utf8.ToNullTerminated(name);
            long width, height;
            fixed (byte* k = key)
            {
                openslide.get_associated_image_dimensions(osr, k, &width, &height);
            }

            result.Add(new AssociatedImage(_slide, name, new SlideSize(width, height)));
        }

        return result;
    }
}
