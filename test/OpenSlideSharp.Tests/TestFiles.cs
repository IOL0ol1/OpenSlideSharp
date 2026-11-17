using System;
using System.IO;

namespace OpenSlideSharp.Tests;

internal static class TestFiles
{
    public static string Get(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
