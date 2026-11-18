using System;
using System.IO;
using System.Threading;

namespace OpenSlideSharp.Viewer.Tests;

internal static class TestFiles
{
    public static string Get(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}

internal static class Wait
{
    /// <summary>Polls <paramref name="condition"/> until it holds, failing the test after <paramref name="seconds"/>.</summary>
    public static void Until(Func<bool> condition, string what, double seconds = 5)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting until " + what + ".");
            }

            Thread.Sleep(2);
        }
    }
}
