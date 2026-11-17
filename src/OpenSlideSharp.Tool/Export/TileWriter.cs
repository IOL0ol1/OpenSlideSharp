using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSlideSharp.Tool.Export;

/// <summary>
/// Writes the files of an export: work items run on a fixed number of worker threads, every file is written
/// atomically, and progress goes to stderr.
/// </summary>
internal sealed class TileWriter
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    private readonly int _jobs;
    private readonly TextWriter _stderr;
    private readonly Action<string>? _fileWritten;

    /// <param name="jobs">The number of worker threads.</param>
    /// <param name="stderr">Where progress lines go.</param>
    /// <param name="fileWritten">Called with the path of each file once it is in place; a test hook.</param>
    public TileWriter(int jobs, TextWriter stderr, Action<string>? fileWritten)
    {
        if (jobs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jobs), jobs, "At least one job is needed.");
        }

        _jobs = jobs;
        _stderr = stderr;
        _fileWritten = fileWritten;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <c>.&lt;name&gt;.tmp</c> next to <paramref name="path"/> and moves it into
    /// place, so <paramref name="path"/> never holds a partial file. The directory must exist.
    /// </summary>
    public void WriteFile(string path, ReadOnlySpan<byte> content)
    {
        string temp = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "." + Path.GetFileName(path) + ".tmp");
        try
        {
            File.WriteAllBytes(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }

        _fileWritten?.Invoke(path);
    }

    /// <summary>
    /// Starts reporting the progress of one image: <c>Tiling &lt;name&gt;: &lt;done&gt;/&lt;total&gt; tiles</c> on stderr,
    /// at most once a second, and once more when all tiles are done.
    /// </summary>
    public Progress StartProgress(string name, long total) => new(name, total, _stderr);

    /// <summary>
    /// Runs <paramref name="process"/> on every item, on the worker threads. Each worker takes the next item in
    /// order, so neighboring items are processed at about the same time.
    /// </summary>
    /// <param name="progress">Counts the tiles the items write.</param>
    /// <param name="items">The work items.</param>
    /// <param name="process">
    /// Processes one item and returns the number of tiles it wrote. It gets a token that is canceled when the export is
    /// canceled or another item failed; it should stop between files then, and must not leave partial files.
    /// </param>
    /// <param name="cancellationToken">Stops the workers after the files they are writing.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <remarks>If an item fails, the other workers stop too, and the first exception is rethrown once all have stopped.</remarks>
    public async Task RunAsync<T>(
        Progress progress, IEnumerable<T> items, Func<T, CancellationToken, long> process, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IEnumerator<T> source = items.GetEnumerator();
        var sourceLock = new object();
        Exception? failure = null;

        void Work()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    T item;
                    lock (sourceLock)
                    {
                        if (!source.MoveNext())
                        {
                            return;
                        }

                        item = source.Current;
                    }

                    progress.Add(process(item, stop.Token));
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                stop.Cancel();
            }
        }

        var workers = new Task[_jobs];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        // Work catches everything, so this only waits; nothing touches the source after it returns.
        await Task.WhenAll(workers).ConfigureAwait(false);

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>The tile count of one image, reported to stderr. Thread-safe.</summary>
    internal sealed class Progress
    {
        private readonly string _name;
        private readonly long _total;
        private readonly TextWriter _stderr;
        private readonly object _lock = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _done;
        private TimeSpan _lastReport;

        internal Progress(string name, long total, TextWriter stderr)
        {
            _name = name;
            _total = total;
            _stderr = stderr;
        }

        /// <summary>Counts <paramref name="tiles"/> more tiles as done.</summary>
        public void Add(long tiles)
        {
            if (tiles <= 0)
            {
                return;
            }

            lock (_lock)
            {
                _done += tiles;
                TimeSpan now = _clock.Elapsed;
                if (_done == _total || now - _lastReport >= ProgressInterval)
                {
                    _stderr.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Tiling {_name}: {_done}/{_total} tiles"));
                    _lastReport = now;
                }
            }
        }
    }
}
