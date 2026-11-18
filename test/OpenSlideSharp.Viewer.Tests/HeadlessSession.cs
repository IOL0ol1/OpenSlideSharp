using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Xunit;

namespace OpenSlideSharp.Viewer.Tests;

/// <summary>
/// The Avalonia headless session the UI tests share: one viewer <see cref="App"/> for the whole test assembly,
/// drawn by Skia so that rendered frames can be captured. Anything that touches Avalonia runs on the session's
/// dispatcher thread through <see cref="Run{T}(Func{T})"/>; tests close the windows they open.
/// </summary>
internal static class HeadlessSession
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    // HeadlessUnitTestSession.StartNew(typeof(App)) would draw with the headless stub, which cannot capture
    // frames, so the session starts from HeadlessApp.BuildAvaloniaApp instead.
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(
        () => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp), AvaloniaTestIsolationLevel.PerAssembly));

    /// <summary>Runs <paramref name="action"/> on the UI thread of the session and returns its result.</summary>
    public static T Run<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Task<T> task = Session.Value.Dispatch(action, TestContext.Current.CancellationToken);
        return task.WaitAsync(RunTimeout).GetAwaiter().GetResult();
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread of the session.</summary>
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Run(() =>
        {
            action();
            return 0;
        });
    }

    /// <summary>
    /// On the UI thread: runs the dispatcher's jobs and render ticks until <paramref name="condition"/> holds,
    /// failing the test after <paramref name="seconds"/>.
    /// </summary>
    public static void PumpUntil(Func<bool> condition, string what, double seconds = 5)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            Pump();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting until " + what + ".");
            }

            Thread.Sleep(2);
        }
    }

    /// <summary>On the UI thread: runs the dispatcher's jobs and one render tick.</summary>
    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}

/// <summary>The viewer's <see cref="App"/> on the headless platform, drawn by Skia.</summary>
internal static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
