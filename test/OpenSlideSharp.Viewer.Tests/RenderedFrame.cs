using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace OpenSlideSharp.Viewer.Tests;

/// <summary>A frame the headless renderer drew, copied out as RGB in device pixels.</summary>
internal sealed class RenderedFrame
{
    private readonly byte[] _rgba;

    private RenderedFrame(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        _rgba = rgba;
    }

    public int Width { get; }

    public int Height { get; }

    public (byte R, byte G, byte B) this[int x, int y]
    {
        get
        {
            int i = ((y * Width) + x) * 4;
            return (_rgba[i], _rgba[i + 1], _rgba[i + 2]);
        }
    }

    /// <summary>On the UI thread: renders <paramref name="topLevel"/> and copies the frame.</summary>
    public static RenderedFrame Capture(TopLevel topLevel)
    {
        using WriteableBitmap bitmap = topLevel.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("Nothing was rendered.");
        using ILockedFramebuffer buffer = bitmap.Lock();
        bool bgra = buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        if (!bgra && buffer.Format != Avalonia.Platform.PixelFormat.Rgba8888)
        {
            throw new NotSupportedException($"Frames in {buffer.Format} are not supported.");
        }

        int width = buffer.Size.Width;
        int height = buffer.Size.Height;
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(buffer.Address + (y * buffer.RowBytes), rgba, y * width * 4, width * 4);
        }

        if (bgra)
        {
            for (int i = 0; i < rgba.Length; i += 4)
            {
                (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
            }
        }

        return new RenderedFrame(width, height, rgba);
    }

    public static bool IsWhite((byte R, byte G, byte B) c) => c.R >= 245 && c.G >= 245 && c.B >= 245;

    public static bool IsRed((byte R, byte G, byte B) c) => c.R >= 200 && c.G <= 60 && c.B <= 60;

    public static bool IsBlue((byte R, byte G, byte B) c) => c.R <= 60 && c.G <= 60 && c.B >= 200;

    public static bool IsGreen((byte R, byte G, byte B) c) => c.R <= 60 && c.G >= 200 && c.B <= 60;

    /// <summary>The selection outline colour, #FFD000.</summary>
    public static bool IsSelectionYellow((byte R, byte G, byte B) c) =>
        Math.Abs(c.R - 0xFF) <= 8 && Math.Abs(c.G - 0xD0) <= 8 && c.B <= 8;

    public int Count(Func<(byte R, byte G, byte B), bool> match)
    {
        int count = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (match(this[x, y]))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>The smallest rectangle holding every matching pixel (right and bottom exclusive), or null.</summary>
    public (int Left, int Top, int Right, int Bottom)? BoundsOf(Func<(byte R, byte G, byte B), bool> match)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (match(this[x, y]))
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            }
        }

        return left == int.MaxValue ? null : (left, top, right, bottom);
    }
}
