using System;
using System.IO;
using System.Text.Json;
using SkiaSharp;

namespace OpenSlideSharp.Tests;

/// <summary>Loads the committed openslide-python / Pillow oracle data.</summary>
internal static class OracleData
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string Path(string relative) => System.IO.Path.Combine(AppContext.BaseDirectory, "Oracles", relative);

    public static T Load<T>(string jsonFile)
    {
        string json = File.ReadAllText(Path(jsonFile));
        return JsonSerializer.Deserialize<T>(json, Options)!;
    }

    /// <summary>Decodes a PNG from Oracles/tiles into opaque BGRA with no color management.</summary>
    public static (int Width, int Height, byte[] Bgra) LoadPng(string file)
    {
        using var codec = SKCodec.Create(Path("tiles/" + file))
            ?? throw new InvalidOperationException("Cannot decode " + file);
        int w = codec.Info.Width, h = codec.Info.Height;
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        byte[] bgra = new byte[w * h * 4];
        var result = codec.GetPixels(info, bgra);
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            throw new InvalidOperationException("Decode failed: " + result);
        return (w, h, bgra);
    }
}
