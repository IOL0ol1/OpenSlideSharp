using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenSlideSharp.Viewer.Core;

/// <summary>The selections drawn on the slide, in the order they were made. Used on the UI thread only.</summary>
internal sealed class SelectionList
{
    private readonly List<Selection> _items = new();

    public SelectionList()
    {
        Items = new ReadOnlyCollection<Selection>(_items);
    }

    public IReadOnlyList<Selection> Items { get; }

    /// <summary>Raised after the list changes.</summary>
    public event Action? Changed;

    public void Add(Selection s)
    {
        ArgumentNullException.ThrowIfNull(s);
        _items.Add(s);
        Changed?.Invoke();
    }

    public void RemoveAt(int i)
    {
        _items.RemoveAt(i);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0)
        {
            return;
        }

        _items.Clear();
        Changed?.Invoke();
    }
}
