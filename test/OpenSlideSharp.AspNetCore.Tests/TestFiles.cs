using System;
using System.IO;

namespace OpenSlideSharp.AspNetCore.Tests;

internal static class TestFiles
{
    public static string Get(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}

/// <summary>A new empty directory under the system temp directory, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openslidesharp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Copies a fixture to <paramref name="relativePath"/> (with '/' separators), creating directories.</summary>
    public string CopyFixture(string fixture, string relativePath)
    {
        string destination = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        File.Copy(TestFiles.Get(fixture), destination);
        return destination;
    }

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
