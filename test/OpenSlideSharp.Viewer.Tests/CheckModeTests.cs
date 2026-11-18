using System;
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using Xunit;

namespace OpenSlideSharp.Viewer.Tests;

public class CheckModeTests
{
    [Theory]
    [InlineData("small.svs", "slide=16x16")]
    [InlineData("boxes.tiff", "slide=300x250")]
    public void Check_OpensSlide_PrintsVersionAndSize(string fixture, string size)
    {
        var output = new StringWriter();

        int code = Program.RunCheck(["--check", TestFiles.Get(fixture)], output);

        Assert.True(code == 0, output.ToString());
        Assert.Contains("openslide=4.", output.ToString());
        Assert.Contains(size, output.ToString());
    }

    [Theory]
    [InlineData("unopenable.tiff")]
    [InlineData("missing.svs")]
    public void Check_Unopenable_Returns1(string fixture)
    {
        var output = new StringWriter();

        int code = Program.RunCheck(["--check", TestFiles.Get(fixture)], output);

        Assert.Equal(1, code);
        Assert.Contains(fixture, output.ToString());
        Assert.DoesNotContain(
            output.ToString().Split(Environment.NewLine),
            line => line.StartsWith("slide=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData]
    [InlineData("--check")]
    [InlineData("--check", "a.svs", "b.svs")]
    public void Check_BadArguments_Returns2(params string[] args)
    {
        var output = new StringWriter();

        int code = Program.RunCheck(args, output);

        Assert.Equal(2, code);
        Assert.Contains("--check <slide>", output.ToString());
    }

    [Theory]
    [InlineData(true, 0L, true)]        // No handle: Windows started the GUI program from a console without redirecting.
    [InlineData(true, -1L, true)]       // INVALID_HANDLE_VALUE.
    [InlineData(true, 0x1234L, false)]  // A pipe or a file the caller redirected the output to.
    [InlineData(false, 0L, false)]      // Not Windows: standard output is always usable.
    public void ParentConsole_AttachesOnlyWithoutAnOutputHandleOnWindows(bool isWindows, long handle, bool expected)
    {
        Assert.Equal(expected, ParentConsole.ShouldAttach(isWindows, new IntPtr(handle)));
    }

    [Fact]
    public void Check_WindowsExecutable_PrintsToRedirectedOutput()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "Only Windows starts the viewer as a GUI program without a console; eng/Test-Packages.ps1 runs the published viewer on every platform");
        string exe = Path.Combine(AppContext.BaseDirectory, "OpenSlideSharp.Viewer.exe");
        using (var pe = new PEReader(File.OpenRead(exe)))
        {
            Assert.Equal(Subsystem.WindowsGui, pe.PEHeaders.PEHeader!.Subsystem);
        }

        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true };
        info.ArgumentList.Add("--check");
        info.ArgumentList.Add(TestFiles.Get("small.svs"));
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();

        Assert.True(process.WaitForExit(30_000), "the viewer did not exit");
        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("openslide=4.", output);
        Assert.Contains("slide=16x16", output);
    }
}
