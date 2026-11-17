using System;
using System.IO;

namespace OpenSlideSharp.Tool.Tests;

internal static class TestFiles
{
    public static string Get(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}

/// <summary>A new empty directory under the system temp directory, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openslidesharp-tool-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relativePath) => System.IO.Path.Combine(Path, relativePath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
