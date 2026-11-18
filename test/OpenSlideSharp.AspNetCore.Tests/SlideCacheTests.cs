using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenSlideSharp.SkiaSharp;
using Xunit;

namespace OpenSlideSharp.AspNetCore.Tests;

public class SlideCacheTests
{
    private static SlideCache CreateCache(Action<OpenSlideServerOptions>? configure = null, ILogger<SlideCache>? logger = null)
    {
        var options = new OpenSlideServerOptions();
        configure?.Invoke(options);
        return new SlideCache(Options.Create(options), logger ?? NullLogger<SlideCache>.Instance);
    }

    // A copy of boxes.tiff whose ICC profile claims a gray data color space, which SkiaColorTransform cannot convert.
    private static string WriteUnconvertibleProfileSlide(TempDirectory directory)
    {
        byte[] profile;
        using (OpenSlide slide = OpenSlide.Open(TestFiles.Get("boxes.tiff")))
        {
            profile = slide.ColorProfile.ToArray();
        }

        byte[] file = File.ReadAllBytes(TestFiles.Get("boxes.tiff"));
        int copies = 0;
        for (int at = file.AsSpan().IndexOf(profile); at >= 0; copies++)
        {
            "GRAY"u8.CopyTo(file.AsSpan(at + 16)); // the data color space field of the profile header
            int next = file.AsSpan(at + 1).IndexOf(profile);
            at = next < 0 ? -1 : at + 1 + next;
        }

        Assert.True(copies > 0);
        string path = Path.Combine(directory.Path, "gray-profile.tiff");
        File.WriteAllBytes(path, file);
        return path;
    }

    // Dedicated threads released together by a barrier, so every TryAcquire really races for the first open.
    private static async Task<SlideLease?[]> AcquireConcurrently(SlideCache cache, string path, int count)
    {
        using var start = new Barrier(count);
        Task<SlideLease?>[] tasks = Enumerable.Range(0, count).Select(_ => Task.Factory.StartNew(
            () =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                return cache.TryAcquire(path);
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public void SlideCache_SameFile_ReturnsSameEntry()
    {
        using SlideCache cache = CreateCache();
        string path = TestFiles.Get("boxes.tiff");

        using SlideLease first = cache.TryAcquire(path)!;
        using SlideLease second = cache.TryAcquire(path)!;

        Assert.NotNull(first);
        Assert.Same(first.Entry, second.Entry);
        Assert.Equal(Path.GetFullPath(path), first.Entry.FilePath);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task SlideCache_ConcurrentFirstAccess_OpensOnce()
    {
        using SlideCache cache = CreateCache();
        string path = TestFiles.Get("boxes.tiff");

        SlideLease?[] leases = await AcquireConcurrently(cache, path, 64);

        try
        {
            Assert.All(leases, lease => Assert.NotNull(lease));
            SlideEntry entry = leases[0]!.Entry;
            Assert.All(leases, lease => Assert.Same(entry, lease!.Entry));
            Assert.Equal(1, cache.OpenCount);
        }
        finally
        {
            foreach (SlideLease? lease in leases)
            {
                lease?.Dispose();
            }
        }
    }

    [Fact]
    public void SlideCache_OpenFailure_NotCached()
    {
        using SlideCache cache = CreateCache();

        Assert.Null(cache.TryAcquire(TestFiles.Get("does-not-exist.tiff")));
        Assert.Equal(0, cache.Count);

        Assert.Null(cache.TryAcquire(TestFiles.Get("unopenable.tiff")));
        Assert.Equal(0, cache.Count);

        // A failed open is retried on the next request rather than remembered.
        Assert.Null(cache.TryAcquire(TestFiles.Get("unopenable.tiff")));
        Assert.Equal(3, cache.OpenCount);
    }

    [Fact]
    public async Task SlideCache_ConcurrentOpenFailure_AllWaitersGetNull()
    {
        using SlideCache cache = CreateCache();
        string path = TestFiles.Get("unopenable.tiff");

        SlideLease?[] leases = await AcquireConcurrently(cache, path, 32);

        Assert.All(leases, lease => Assert.Null(lease));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void SlideCache_EvictionDuringLease_DefersDispose()
    {
        using SlideCache cache = CreateCache(o => o.SlideCacheSize = 1);

        SlideLease leaseA = cache.TryAcquire(TestFiles.Get("boxes.tiff"))!;
        OpenSlide slideA = leaseA.Entry.Slide;

        using (SlideLease leaseB = cache.TryAcquire(TestFiles.Get("small.svs"))!)
        {
            Assert.NotNull(leaseB);
            Assert.Equal(1, cache.Count);

            // A is out of the cache but still leased, so it stays open.
            Assert.Equal(16, slideA.ReadRegion(0, 0, 0, 2, 2).Data.Length);
        }

        leaseA.Dispose();
        Assert.Throws<ObjectDisposedException>(() => slideA.ReadRegion(0, 0, 0, 2, 2));
    }

    [Fact]
    public void SlideCache_EvictedWithoutLease_DisposesAtOnce()
    {
        using SlideCache cache = CreateCache(o => o.SlideCacheSize = 1);

        OpenSlide slideA;
        using (SlideLease leaseA = cache.TryAcquire(TestFiles.Get("boxes.tiff"))!)
        {
            slideA = leaseA.Entry.Slide;
        }

        Assert.Equal(16, slideA.ReadRegion(0, 0, 0, 2, 2).Data.Length);
        using SlideLease leaseB = cache.TryAcquire(TestFiles.Get("small.svs"))!;
        Assert.Throws<ObjectDisposedException>(() => slideA.ReadRegion(0, 0, 0, 2, 2));
    }

    [Fact]
    public void SlideLease_DisposeTwice_ReturnsTheLeaseOnce()
    {
        using SlideCache cache = CreateCache(o => o.SlideCacheSize = 1);
        string path = TestFiles.Get("boxes.tiff");

        SlideLease first = cache.TryAcquire(path)!;
        SlideLease second = cache.TryAcquire(path)!;
        OpenSlide slide = first.Entry.Slide;
        using (cache.TryAcquire(TestFiles.Get("small.svs")))
        {
        }

        first.Dispose();
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Entry);

        // The second lease still holds the slide open.
        Assert.Equal(16, slide.ReadRegion(0, 0, 0, 2, 2).Data.Length);
        second.Dispose();
        Assert.Throws<ObjectDisposedException>(() => slide.ReadRegion(0, 0, 0, 2, 2));
    }

    [Fact]
    public void SlideCache_FailedOpens_DoNotEvictCachedSlides()
    {
        // In directory mode anyone can ask for files that are not slides (MRXS Data*.dat, notes.txt, ...); those
        // requests must not push healthy slides out of the cache.
        using var directory = new TempDirectory();
        string notes = Path.Combine(directory.Path, "notes.txt");
        File.WriteAllText(notes, "not a slide");
        using SlideCache cache = CreateCache(o => o.SlideCacheSize = 1);
        string path = TestFiles.Get("boxes.tiff");

        SlideEntry before;
        using (SlideLease lease = cache.TryAcquire(path)!)
        {
            before = lease.Entry;
        }

        for (int i = 0; i < 5; i++)
        {
            Assert.Null(cache.TryAcquire(notes));
            Assert.Null(cache.TryAcquire(TestFiles.Get("unopenable.tiff")));
            Assert.Null(cache.TryAcquire(TestFiles.Get("does-not-exist.tiff")));
        }

        Assert.Equal(1, cache.Count);
        using SlideLease after = cache.TryAcquire(path)!;
        Assert.Same(before, after.Entry);
        Assert.Equal(16, after.Entry.Slide.ReadRegion(0, 0, 0, 2, 2).Data.Length);

        // boxes.tiff was opened once; every failed open was tried.
        Assert.Equal(1 + 15, cache.OpenCount);
    }

    [Fact]
    public async Task SlideCache_ConcurrentChurn_EveryLeaseReads()
    {
        // Capacity 1, two slides, 32 threads: slides are evicted all the time while leases still read them.
        using SlideCache cache = CreateCache(o => o.SlideCacheSize = 1);
        string[] paths = [TestFiles.Get("boxes.tiff"), TestFiles.Get("boxes_0.dcm")];
        const int Threads = 32;
        using var start = new Barrier(Threads);
        var failures = new ConcurrentQueue<string>();

        Task[] tasks = Enumerable.Range(0, Threads).Select(t => Task.Factory.StartNew(
            () =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                for (int i = 0; i < 40; i++)
                {
                    string path = paths[(t + i) % 2];
                    try
                    {
                        using SlideLease? lease = cache.TryAcquire(path);
                        if (lease is null)
                        {
                            failures.Enqueue($"{path}: no lease");
                        }
                        else if (lease.Entry.Slide.ReadRegion(0, 0, 0, 2, 2).Data.Length != 16)
                        {
                            failures.Enqueue($"{path}: wrong region size");
                        }
                    }
                    catch (Exception e)
                    {
                        failures.Enqueue($"{path}: {e.GetType().Name}: {e.Message}");
                    }
                }
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();
        await Task.WhenAll(tasks);

        Assert.Empty(failures);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void SlideCache_Constructor_ValidatesOptions()
    {
        var options = new OpenSlideServerOptions();
        options.DeepZoom.TileSize = 0;

        var e = Assert.Throws<OptionsValidationException>(() => new SlideCache(Options.Create(options), NullLogger<SlideCache>.Instance));
        Assert.Contains("TileSize", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SlideCache_Constructor_SnapshotsOptions()
    {
        var options = new OpenSlideServerOptions();
        using var cache = new SlideCache(Options.Create(options), NullLogger<SlideCache>.Instance);

        // Changes after the cache was created have no effect, valid or not.
        options.SlideCacheSize = 0;
        options.ColorMode = ColorMode.Embed;
        options.DeepZoom.TileSize = 126;
        options.OmeZarr.ChunkSize = 64;

        using SlideLease lease = cache.TryAcquire(TestFiles.Get("boxes.tiff"))!;
        Assert.Equal(ColorMode.Ignore, lease.Entry.EncodeMode(null));
        Assert.Equal(254, lease.Entry.GetDeepZoom(null).TileSize);
        Assert.Equal(512, lease.Entry.GetOmeZarr(null).ChunkSize);
    }

    [Fact]
    public void SlideCache_Evict_ReopensNextTime()
    {
        using SlideCache cache = CreateCache();
        string path = TestFiles.Get("boxes.tiff");

        SlideEntry before;
        using (SlideLease lease = cache.TryAcquire(path)!)
        {
            before = lease.Entry;
        }

        cache.Evict(path);
        Assert.Equal(0, cache.Count);

        using SlideLease after = cache.TryAcquire(path)!;
        Assert.NotSame(before, after.Entry);
        Assert.Equal(2, cache.OpenCount);
        Assert.Throws<ObjectDisposedException>(() => before.Slide.ReadRegion(0, 0, 0, 2, 2));
    }

    [Fact]
    public void SlideCache_EvictByStaleLease_KeepsNewerInstance()
    {
        using SlideCache cache = CreateCache();
        string path = TestFiles.Get("boxes.tiff");

        // A request holds the slide when another one evicts it (say, after an OpenSlide error); a third reopens it.
        SlideLease stale = cache.TryAcquire(path)!;
        cache.Evict(path);
        using SlideLease fresh = cache.TryAcquire(path)!;
        Assert.NotSame(stale.Entry, fresh.Entry);

        // The first request now evicts what it used: only that instance, not the newer one.
        cache.Evict(stale);
        stale.Dispose();
        cache.Evict(stale); // a disposed lease evicts nothing
        Assert.Equal(1, cache.Count);
        using (SlideLease again = cache.TryAcquire(path)!)
        {
            Assert.Same(fresh.Entry, again.Entry);
        }

        Assert.Equal(2, cache.OpenCount);

        // The lease of the cached instance does evict it.
        cache.Evict(fresh);
        Assert.Equal(0, cache.Count);
        Assert.Equal(16, fresh.Entry.Slide.ReadRegion(0, 0, 0, 2, 2).Data.Length);
    }

    [Fact]
    public void SlideCache_TileCache_IsSetOnEverySlide()
    {
        using (SlideCache cache = CreateCache())
        {
            using SlideLease boxes = cache.TryAcquire(TestFiles.Get("boxes.tiff"))!;
            using SlideLease small = cache.TryAcquire(TestFiles.Get("small.svs"))!;
            Assert.Null(cache.TryAcquire(TestFiles.Get("unopenable.tiff")));

            Assert.Equal(2, cache.TileCacheAssignments);
            Assert.Equal(16, boxes.Entry.Slide.ReadRegion(0, 0, 0, 2, 2).Data.Length);
            Assert.Equal(16, small.Entry.Slide.ReadRegion(0, 0, 0, 2, 2).Data.Length);
        }

        // 0 keeps OpenSlide's own cache of each slide.
        using (SlideCache cache = CreateCache(o => o.TileCacheSize = 0))
        using (SlideLease boxes = cache.TryAcquire(TestFiles.Get("boxes.tiff"))!)
        {
            Assert.Equal(0, cache.TileCacheAssignments);
            Assert.Equal(16, boxes.Entry.Slide.ReadRegion(0, 0, 0, 2, 2).Data.Length);
        }
    }

    [Fact]
    public void SlideCache_KeyIgnoresCaseOnWindowsOnly()
    {
        using SlideCache cache = CreateCache();
        string path = TestFiles.Get("boxes.tiff");
        string upper = Path.Combine(Path.GetDirectoryName(path)!, "BOXES.TIFF");

        using SlideLease lease = cache.TryAcquire(path)!;
        if (OperatingSystem.IsWindows())
        {
            using SlideLease other = cache.TryAcquire(upper)!;
            Assert.Same(lease.Entry, other.Entry);
        }
        else
        {
            using SlideLease? other = cache.TryAcquire(upper);
            Assert.True(other is null || !ReferenceEquals(lease.Entry, other.Entry));
        }
    }

    [Fact]
    public void SlideCache_Dispose_ClosesIdleSlides()
    {
        SlideCache cache = CreateCache();
        OpenSlide slide;
        using (SlideLease lease = cache.TryAcquire(TestFiles.Get("boxes.tiff"))!)
        {
            slide = lease.Entry.Slide;
        }

        cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => slide.ReadRegion(0, 0, 0, 2, 2));
        Assert.Throws<ObjectDisposedException>(() => cache.TryAcquire(TestFiles.Get("boxes.tiff")));
    }

    [Fact]
    public void Entry_ColorMode_DefaultWithIcc_UsesTransform()
    {
        string path = TestFiles.Get("boxes.tiff");

        using (SlideCache cache = CreateCache())
        using (SlideLease lease = cache.TryAcquire(path)!)
        {
            SlideEntry entry = lease.Entry;
            Assert.Equal(588, entry.Slide.ColorProfile.Length);
            Assert.Equal(ColorMode.Ignore, entry.EncodeMode(null));

            DeepZoomGenerator dz = entry.GetDeepZoom(null);
            Assert.Same(dz, entry.GetDeepZoom(null));
            Assert.True(dz.GetTile(dz.LevelCount - 1, 0, 0).IccProfile.IsEmpty);
        }

        using (SlideCache cache = CreateCache(o => o.ColorMode = ColorMode.Embed))
        using (SlideLease lease = cache.TryAcquire(path)!)
        {
            SlideEntry entry = lease.Entry;
            Assert.Equal(ColorMode.Embed, entry.EncodeMode(null));

            DeepZoomGenerator dz = entry.GetDeepZoom(null);
            Assert.Equal(588, dz.GetTile(dz.LevelCount - 1, 0, 0).IccProfile.Length);
        }

        using (SlideCache cache = CreateCache(o => o.ColorMode = ColorMode.Ignore))
        using (SlideLease lease = cache.TryAcquire(path)!)
        {
            Assert.Equal(ColorMode.Ignore, lease.Entry.EncodeMode(null));
        }
    }

    [Fact]
    public void Entry_DefaultWithUnconvertibleIcc_EmbedsAndWarnsOnce()
    {
        using var directory = new TempDirectory();
        string path = WriteUnconvertibleProfileSlide(directory);
        var logger = new ListLogger<SlideCache>();
        using SlideCache cache = CreateCache(logger: logger);
        using SlideLease lease = cache.TryAcquire(path)!;
        SlideEntry entry = lease.Entry;
        Assert.Null(SkiaColorTransform.Create(entry.Slide.ColorProfile));

        // Deep Zoom falls back to Default encoding, which embeds the profile; OME-Zarr leaves the pixels as they are.
        Assert.Equal(ColorMode.Default, entry.EncodeMode(null));
        DeepZoomGenerator dz = entry.GetDeepZoom(null);
        Assert.Equal(588, dz.GetTile(dz.LevelCount - 1, 0, 0).IccProfile.Length);
        Assert.Equal(ColorMode.Default, entry.EncodeMode(null));
        entry.GetOmeZarr(null);
        entry.GetOmeZarr(null);

        string warning = Assert.Single(logger.Messages(LogLevel.Warning));
        Assert.Contains(Path.GetFullPath(path), warning, StringComparison.Ordinal);
        Assert.Contains("the slide", warning, StringComparison.Ordinal);
        Assert.Contains("cannot be converted", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_EmbedWithOmeZarr_WarnsOncePerImage()
    {
        var logger = new ListLogger<SlideCache>();
        using (SlideCache cache = CreateCache(o => o.ColorMode = ColorMode.Embed, logger))
        using (SlideLease lease = cache.TryAcquire(TestFiles.Get("boxes.tiff"))!)
        {
            lease.Entry.GetDeepZoom(null);
            Assert.Empty(logger.Messages(LogLevel.Warning));

            lease.Entry.GetOmeZarr(null);
            lease.Entry.GetOmeZarr(null);
            string warning = Assert.Single(logger.Messages(LogLevel.Warning));
            Assert.Contains("boxes.tiff", warning, StringComparison.Ordinal);
            Assert.Contains("the slide", warning, StringComparison.Ordinal);
            Assert.Contains("OME-Zarr cannot embed", warning, StringComparison.Ordinal);
        }

        // A convertible profile in the default mode, and a failed open, give no warning.
        var quiet = new ListLogger<SlideCache>();
        using (SlideCache cache = CreateCache(logger: quiet))
        {
            using (SlideLease lease = cache.TryAcquire(TestFiles.Get("boxes.tiff"))!)
            {
                lease.Entry.GetDeepZoom(null);
                lease.Entry.GetOmeZarr(null);
            }

            Assert.Null(cache.TryAcquire(TestFiles.Get("unopenable.tiff")));
        }

        Assert.Empty(quiet.Messages(LogLevel.Warning));
        Assert.Contains(quiet.Messages(LogLevel.Debug), m => m.Contains("unopenable.tiff", StringComparison.Ordinal));
    }

    [Fact]
    public void Entry_UsesServerOptions()
    {
        using SlideCache cache = CreateCache(o =>
        {
            o.DeepZoom.TileSize = 126;
            o.DeepZoom.Overlap = 2;
            o.DeepZoom.LimitBounds = false;
            o.OmeZarr.ChunkSize = 64;
            o.OmeZarr.Version = NgffVersion.V0_4;
            o.OmeZarr.Compression = OmeZarrCompression.Gzip;
            o.OmeZarr.GzipLevel = 3;
        });
        using SlideLease lease = cache.TryAcquire(TestFiles.Get("boxes.tiff"))!;

        DeepZoomGenerator dz = lease.Entry.GetDeepZoom(null);
        Assert.Equal(126, dz.TileSize);
        Assert.Equal(2, dz.Overlap);
        Assert.False(dz.LimitBounds);

        OmeZarrGenerator ome = lease.Entry.GetOmeZarr(null);
        Assert.Same(ome, lease.Entry.GetOmeZarr(null));
        Assert.Equal(64, ome.ChunkSize);
        Assert.Equal(NgffVersion.V0_4, ome.Version);
        Assert.Contains("compression: Gzip(3)", ome.ToString(), StringComparison.Ordinal);

        // OME-Zarr has its own LimitBounds, still the default true.
        Assert.Contains("limitBounds: True", ome.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_AssociatedImage_UsesLimitBoundsFalse()
    {
        using SlideCache cache = CreateCache();
        using SlideLease lease = cache.TryAcquire(TestFiles.Get("small.svs"))!;
        SlideEntry entry = lease.Entry;

        OmeZarrGenerator thumbnail = entry.GetOmeZarr("thumbnail");
        Assert.Contains("limitBounds: False", thumbnail.ToString(), StringComparison.Ordinal);
        Assert.Same(thumbnail, entry.GetOmeZarr("thumbnail"));
        Assert.False(entry.GetDeepZoom("thumbnail").LimitBounds);

        // The main image keeps the server setting.
        Assert.Contains("limitBounds: True", entry.GetOmeZarr(null).ToString(), StringComparison.Ordinal);
        Assert.True(entry.GetDeepZoom(null).LimitBounds);
    }

    [Fact]
    public void Entry_UnknownAssociatedImage_ThrowsKeyNotFound()
    {
        using SlideCache cache = CreateCache();
        using SlideLease lease = cache.TryAcquire(TestFiles.Get("small.svs"))!;

        Assert.Throws<KeyNotFoundException>(() => lease.Entry.GetDeepZoom("nope"));
        Assert.Throws<KeyNotFoundException>(() => lease.Entry.GetOmeZarr("nope"));
        Assert.Throws<KeyNotFoundException>(() => lease.Entry.EncodeMode("nope"));
    }

    [Theory]
    [InlineData("SlideCacheSize", 0)]
    [InlineData("TileSize", 0)]
    [InlineData("Overlap", -1)]
    [InlineData("Quality", -1)]
    [InlineData("Quality", 101)]
    [InlineData("ChunkSize", 15)]
    [InlineData("ChunkSize", 8193)]
    [InlineData("GzipLevel", -1)]
    [InlineData("GzipLevel", 10)]
    [InlineData("RenderCacheSize", -1)]
    [InlineData("TileCacheSize", -1)]
    public void Options_Invalid_ThrowOnResolve(string setting, int value)
    {
        var services = new ServiceCollection();
        services.AddOpenSlide(o =>
        {
            switch (setting)
            {
                case "SlideCacheSize": o.SlideCacheSize = value; break;
                case "TileCacheSize": o.TileCacheSize = value; break;
                case "TileSize": o.DeepZoom.TileSize = value; break;
                case "Overlap": o.DeepZoom.Overlap = value; break;
                case "Quality": o.DeepZoom.Quality = value; break;
                case "ChunkSize": o.OmeZarr.ChunkSize = value; break;
                case "GzipLevel": o.OmeZarr.GzipLevel = value; break;
                case "RenderCacheSize": o.OmeZarr.RenderCacheSize = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(setting));
            }
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        var e = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<OpenSlideServerOptions>>().Value);
        Assert.Contains(setting, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_Defaults()
    {
        var services = new ServiceCollection();
        services.AddOpenSlide();
        using ServiceProvider provider = services.BuildServiceProvider();
        OpenSlideServerOptions options = provider.GetRequiredService<IOptions<OpenSlideServerOptions>>().Value;

        Assert.Equal(10, options.SlideCacheSize);
        Assert.Equal(128, options.TileCacheSize);
        Assert.Equal(ColorMode.Default, options.ColorMode);
        Assert.Equal(254, options.DeepZoom.TileSize);
        Assert.Equal(1, options.DeepZoom.Overlap);
        Assert.Equal(ImageFormat.Jpeg, options.DeepZoom.Format);
        Assert.Equal(75, options.DeepZoom.Quality);
        Assert.True(options.DeepZoom.LimitBounds);
        Assert.Equal(NgffVersion.V0_5, options.OmeZarr.Version);
        Assert.Equal(512, options.OmeZarr.ChunkSize);
        Assert.Equal(OmeZarrCompression.None, options.OmeZarr.Compression);
        Assert.Equal(6, options.OmeZarr.GzipLevel);
        Assert.Equal(16, options.OmeZarr.RenderCacheSize);
        Assert.True(options.OmeZarr.LimitBounds);
        Assert.True(options.OmeZarr.AllowCrossOrigin);

        Assert.Same(provider.GetRequiredService<SlideCache>(), provider.GetRequiredService<SlideCache>());
    }

    [Fact]
    public void Options_BindFromConfiguration()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SlideCacheSize"] = "3",
                ["TileCacheSize"] = "64",
                ["ColorMode"] = "embed",
                ["DeepZoom:TileSize"] = "510",
                ["DeepZoom:Format"] = "png",
                ["DeepZoom:LimitBounds"] = "false",
                ["OmeZarr:Version"] = "V0_4",
                ["OmeZarr:AllowCrossOrigin"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenSlide(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        OpenSlideServerOptions options = provider.GetRequiredService<IOptions<OpenSlideServerOptions>>().Value;

        Assert.Equal(3, options.SlideCacheSize);
        Assert.Equal(64, options.TileCacheSize);
        Assert.Equal(ColorMode.Embed, options.ColorMode);
        Assert.Equal(510, options.DeepZoom.TileSize);
        Assert.Equal(ImageFormat.Png, options.DeepZoom.Format);
        Assert.False(options.DeepZoom.LimitBounds);
        Assert.Equal(1, options.DeepZoom.Overlap);
        Assert.Equal(NgffVersion.V0_4, options.OmeZarr.Version);
        Assert.False(options.OmeZarr.AllowCrossOrigin);
    }
}
