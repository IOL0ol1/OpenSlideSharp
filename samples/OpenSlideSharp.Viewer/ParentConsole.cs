using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenSlideSharp.Viewer;

/// <summary>
/// Lets <c>--check</c> print to the console it was started from. On Windows the viewer is a GUI (WinExe) program, so
/// it starts without a console: unless the caller redirects its output to a pipe or a file, standard output goes
/// nowhere. Attaching to the parent process's console makes the lines appear there.
/// </summary>
internal static class ParentConsole
{
    private const int StdOutputHandle = -11;
    private const uint AttachParentProcess = unchecked((uint)-1);

    /// <summary>
    /// Whether to attach to the parent's console: on Windows, when the process has no standard output handle. A
    /// redirected output (a pipe or a file) is a valid handle and is used as it is.
    /// <see cref="Console.IsOutputRedirected"/> cannot decide this: it reports a missing handle as redirected.
    /// </summary>
    internal static bool ShouldAttach(bool isWindows, IntPtr standardOutput) =>
        isWindows && (standardOutput == IntPtr.Zero || standardOutput == new IntPtr(-1));

    /// <summary>
    /// Attaches to the parent's console when <see cref="ShouldAttach"/> says so, and re-opens
    /// <see cref="Console.Out"/> on it. Does nothing if the parent has no console.
    /// </summary>
    internal static void AttachIfNoOutput()
    {
        if (!OperatingSystem.IsWindows() || !ShouldAttach(isWindows: true, GetStdHandle(StdOutputHandle)))
        {
            return;
        }

        if (!AttachConsole(AttachParentProcess))
        {
            return;
        }

        // AttachConsole fills in the standard handles. Console.Out may already be bound to the missing one, so open
        // the console's handle again, writing in the console's code page. A UTF-8 console must not get a byte order
        // mark, which Console.OutputEncoding would write.
        Encoding encoding = Console.OutputEncoding;
        if (encoding.CodePage == Encoding.UTF8.CodePage)
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);
}
