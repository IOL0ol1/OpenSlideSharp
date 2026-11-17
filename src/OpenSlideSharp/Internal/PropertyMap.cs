using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using OpenSlideSharp.Native;

namespace OpenSlideSharp;

/// <summary>
/// A live, read-only view of a slide's properties. Nothing is cached: every lookup and enumeration queries
/// OpenSlide through <see cref="OpenSlide.Invoke{T}(Func{IntPtr, T})"/>, so it throws once the slide is disposed
/// or in the error state.
/// </summary>
internal sealed class PropertyMap : IReadOnlyDictionary<string, string>
{
    private readonly OpenSlide _slide;

    internal PropertyMap(OpenSlide slide) => _slide = slide;

    public string this[string key]
    {
        get
        {
            if (TryGetValue(key, out string? value))
            {
                return value;
            }

            throw new KeyNotFoundException($"The property '{key}' was not found.");
        }
    }

    public int Count => _slide.Invoke(static osr => ReadNames(osr).Count);

    public IEnumerable<string> Keys => _slide.Invoke(static osr => ReadNames(osr));

    public IEnumerable<string> Values => _slide.Invoke(static osr =>
    {
        var names = ReadNames(osr);
        var values = new List<string>(names.Count);
        foreach (string name in names)
        {
            values.Add(ReadValue(osr, name) ?? string.Empty);
        }

        return values;
    });

    public bool ContainsKey(string key) => TryGetValue(key, out _);

#if NET5_0_OR_GREATER
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value)
#else
    // IReadOnlyDictionary<,>.TryGetValue carries no nullable annotations before .NET 5.
    public bool TryGetValue(string key, out string value)
#endif
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        string? found = _slide.Invoke(osr => ReadValue(osr, key));
        value = found!;
        return found is not null;
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        // One snapshot taken inside a single Invoke; the managed copies are safe to enumerate afterwards.
        var pairs = _slide.Invoke(static osr =>
        {
            var names = ReadNames(osr);
            var list = new List<KeyValuePair<string, string>>(names.Count);
            foreach (string name in names)
            {
                list.Add(new KeyValuePair<string, string>(name, ReadValue(osr, name) ?? string.Empty));
            }

            return list;
        });
        return pairs.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // The native array and strings belong to the slide; copy them into managed strings before returning.
    private static unsafe List<string> ReadNames(IntPtr osr)
    {
        var names = new List<string>();
        byte** p = openslide.get_property_names(osr);
        if (p != null)
        {
            for (; *p != null; p++)
            {
                names.Add(Utf8.FromNative(*p) ?? string.Empty);
            }
        }

        return names;
    }

    private static unsafe string? ReadValue(IntPtr osr, string name)
    {
        byte[] key = Utf8.ToNullTerminated(name);
        fixed (byte* k = key)
        {
            return Utf8.FromNative(openslide.get_property_value(osr, k));
        }
    }
}
