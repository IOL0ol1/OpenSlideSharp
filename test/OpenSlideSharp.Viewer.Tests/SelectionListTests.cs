using System;
using OpenSlideSharp.Viewer.Core;
using Xunit;

namespace OpenSlideSharp.Viewer.Tests;

public class SelectionListTests
{
    [Fact]
    public void SelectionList_AddRemoveClear_RaiseChanged()
    {
        var list = new SelectionList();
        int changes = 0;
        list.Changed += () => changes++;
        var a = new Selection(10, 20, 30, 40);
        var b = new Selection(1, 2, 3, 4);

        list.Add(a);
        list.Add(b);
        Assert.Equal([a, b], list.Items);
        Assert.Equal(2, changes);

        list.RemoveAt(0);
        Assert.Equal([b], list.Items);
        Assert.Equal(3, changes);

        list.Clear();
        Assert.Empty(list.Items);
        Assert.Equal(4, changes);
        list.Clear(); // already empty: nothing changes
        Assert.Equal(4, changes);

        Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveAt(0));
        Assert.Throws<ArgumentNullException>(() => list.Add(null!));
        Assert.Equal(4, changes);
    }
}
