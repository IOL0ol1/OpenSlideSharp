using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace OpenSlideSharp.Tests;

/// <summary>
/// Writes OME-Zarr stores and the expected pixels to disk so that eng/verify-omezarr.py can read them back
/// with zarr-python and ome-zarr-py. The test itself only checks that the files were written.
/// </summary>
public class OmeZarrInteropExport
{
    private const int ChunkSize = 64;

    private static readonly (string Name, NgffVersion Version, OmeZarrCompression Compression)[] Stores =
    {
        ("v05-none.zarr", NgffVersion.V0_5, OmeZarrCompression.None),
        ("v05-gzip.zarr", NgffVersion.V0_5, OmeZarrCompression.Gzip),
        ("v04-none.zarr", NgffVersion.V0_4, OmeZarrCompression.None),
        ("v04-gzip.zarr", NgffVersion.V0_4, OmeZarrCompression.Gzip),
    };

    [Fact]
    public void Export_ForPythonInterop()
    {
        string? configured = Environment.GetEnvironmentVariable("OPENSLIDESHARP_INTEROP_DIR");
        bool temporary = string.IsNullOrEmpty(configured);
        string root = temporary
            ? Path.Combine(Path.GetTempPath(), "openslidesharp-interop-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(configured!);

        try
        {
            Directory.CreateDirectory(root);
            using OpenSlide slide = OpenSlide.Open(TestFiles.Get("boxes.tiff"));

            foreach (var (name, version, compression) in Stores)
            {
                var options = new OmeZarrOptions { Version = version, Compression = compression, ChunkSize = ChunkSize };
                var generator = new OmeZarrGenerator(slide, options);
                string storeDir = Path.Combine(root, name);
                foreach (string key in generator.EnumerateKeys())
                {
                    Assert.True(generator.TryGetEntry(key, out byte[]? content), key);
                    string path = Path.Combine(storeDir, key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, content!);
                }
            }

            var levels = new List<string>();
            var reference = new OmeZarrGenerator(slide, new OmeZarrOptions { ChunkSize = ChunkSize });
            var dz = new DeepZoomGenerator(slide, tileSize: ChunkSize, overlap: 0);
            Directory.CreateDirectory(Path.Combine(root, "expected"));
            for (int l = 0; l < reference.LevelCount; l++)
            {
                SlideSize size = reference.LevelDimensions[l];
                int dzLevel = dz.LevelCount - 1 - l;
                Assert.Equal(size, dz.LevelDimensions[dzLevel]);
                int w = checked((int)size.Width), h = checked((int)size.Height);

                // Planar c, y, x uint8.
                var planes = new byte[3 * h * w];
                TileGridSize grid = dz.LevelTiles[dzLevel];
                for (long row = 0; row < grid.Rows; row++)
                {
                    for (long col = 0; col < grid.Columns; col++)
                    {
                        PixelImage tile = dz.GetTile(dzLevel, col, row);
                        int x0 = checked((int)(col * ChunkSize)), y0 = checked((int)(row * ChunkSize));
                        for (int y = 0; y < tile.Height; y++)
                        {
                            for (int x = 0; x < tile.Width; x++)
                            {
                                int src = y * tile.Width * 4 + x * 4; // B, G, R, A
                                int dst = (y0 + y) * w + x0 + x;
                                planes[dst] = tile.Data[src + 2];
                                planes[h * w + dst] = tile.Data[src + 1];
                                planes[2 * h * w + dst] = tile.Data[src];
                            }
                        }
                    }
                }

                File.WriteAllBytes(Path.Combine(root, "expected", l + ".rgb"), planes);
                levels.Add($"[3,{h},{w}]");
            }

            var manifest = new StringBuilder("{\"stores\":[");
            for (int i = 0; i < Stores.Length; i++)
            {
                if (i > 0) manifest.Append(',');
                manifest.Append('"').Append(Stores[i].Name).Append('"');
            }
            manifest.Append("],\"levels\":[").Append(string.Join(",", levels)).Append("]}");
            File.WriteAllText(Path.Combine(root, "manifest.json"), manifest.ToString());

            Assert.True(File.Exists(Path.Combine(root, "manifest.json")));
            foreach (var store in Stores)
            {
                Assert.True(Directory.Exists(Path.Combine(root, store.Name)), store.Name);
            }
            for (int l = 0; l < levels.Count; l++)
            {
                Assert.True(File.Exists(Path.Combine(root, "expected", l + ".rgb")));
            }
        }
        finally
        {
            if (temporary && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
