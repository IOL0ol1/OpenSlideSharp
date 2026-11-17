using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace OpenSlideSharp.AspNetCore.Tests;

public class ResolverTests
{
    // Not URL paths: markers for the cases that need a symbolic link created first.
    private const string FileLinkCase = "<file symlink to a file outside the root>";
    private const string DirectoryLinkCase = "<directory symlink to a directory outside the root>";

    [Fact]
    public void SingleResolver_MatchesOnlyItsName()
    {
        string path = TestFiles.Get("boxes.tiff");
        var resolver = new SingleSlideResolver(path);

        Assert.Equal("slide", resolver.Name);
        Assert.True(resolver.TryResolve("slide", out string? resolved));
        Assert.Equal(Path.GetFullPath(path), resolved);

        foreach (string other in new[] { "other", "", "slide/x" })
        {
            Assert.False(resolver.TryResolve(other, out resolved));
            Assert.Null(resolved);
        }
    }

    [Fact]
    public void DirectoryResolver_ResolvesNestedFile()
    {
        using var root = new TempDirectory();
        string file = root.CopyFixture("boxes.tiff", "a/b/boxes.tiff");
        var resolver = new DirectorySlideResolver(root.Path);

        Assert.Equal(Path.GetFullPath(root.Path), resolver.RootDirectory);
        Assert.True(resolver.TryResolve("a/b/boxes.tiff", out string? resolved));
        Assert.Equal(Path.GetFullPath(file), resolved);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("")]
    [InlineData("a/")]
    [InlineData(FileLinkCase)]
    [InlineData(DirectoryLinkCase)]
    public void DirectoryResolver_RejectsTraversal(string urlPath)
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        root.CopyFixture("boxes.tiff", "a/b/boxes.tiff");
        string outsideFile = outside.CopyFixture("boxes.tiff", "secret/boxes.tiff");

        if (urlPath == FileLinkCase)
        {
            CreateLinkOrSkip(Path.Combine(root.Path, "a", "link.tiff"), outsideFile, isDirectory: false);
            urlPath = "a/link.tiff";
        }
        else if (urlPath == DirectoryLinkCase)
        {
            CreateLinkOrSkip(Path.Combine(root.Path, "a", "linkdir"), Path.GetDirectoryName(outsideFile)!, isDirectory: true);
            urlPath = "a/linkdir/boxes.tiff";
        }

        var resolver = new DirectorySlideResolver(root.Path);
        Assert.False(resolver.TryResolve(urlPath, out string? resolved));
        Assert.Null(resolved);
    }

    [Fact]
    public void DirectoryResolver_FollowsLinkInsideRoot()
    {
        using var root = new TempDirectory();
        root.CopyFixture("boxes.tiff", "a/b/boxes.tiff");
        CreateLinkOrSkip(Path.Combine(root.Path, "c"), Path.Combine(root.Path, "a", "b"), isDirectory: true);
        var resolver = new DirectorySlideResolver(root.Path);

        Assert.True(resolver.TryResolve("c/boxes.tiff", out string? resolved));
        Assert.Equal(Path.Combine(resolver.RootDirectory, "c", "boxes.tiff"), resolved);
    }

    [Fact]
    public void DirectoryResolver_MissingFileOrDirectory_ReturnsFalse()
    {
        using var root = new TempDirectory();
        root.CopyFixture("boxes.tiff", "a/b/boxes.tiff");
        var resolver = new DirectorySlideResolver(root.Path);

        Assert.False(resolver.TryResolve("a/b/missing.tiff", out _));
        Assert.False(resolver.TryResolve("a/b", out _));
        Assert.False(resolver.TryResolve("a/b/boxes.tiff/x", out _));
    }

    private static void CreateLinkOrSkip(string link, string target, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }

            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Windows needs Developer Mode or an elevated process for symbolic links. A directory junction needs
            // neither, and the resolver must treat it like a symbolic link.
            if (isDirectory && OperatingSystem.IsWindows() && TryCreateJunction(link, target))
            {
                return;
            }

            Assert.Skip($"Cannot create a symbolic link here: {e.Message}");
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("mklink");
        info.ArgumentList.Add("/J");
        info.ArgumentList.Add(link);
        info.ArgumentList.Add(target);
        using Process process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
