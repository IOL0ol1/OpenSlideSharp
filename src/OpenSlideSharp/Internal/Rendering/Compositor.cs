using System;
using System.Globalization;

namespace OpenSlideSharp;

/// <summary>Composites premultiplied BGRA pixels over a solid background, matching Pillow paste plus openslide-python.</summary>
internal static class Compositor
{
    /// <summary>Composites premultiplied BGRA pixels in place into opaque BGRA.</summary>
    internal static void CompositeInPlace(Span<byte> premultipliedBgra, byte red, byte green, byte blue)
    {
        for (int i = 0; i + 3 < premultipliedBgra.Length; i += 4)
        {
            int a = premultipliedBgra[i + 3];
            if (a != 255)
            {
                if (a == 0)
                {
                    premultipliedBgra[i] = blue;
                    premultipliedBgra[i + 1] = green;
                    premultipliedBgra[i + 2] = red;
                }
                else
                {
                    premultipliedBgra[i] = Blend(blue, Math.Min(255, 255 * premultipliedBgra[i] / a), a);
                    premultipliedBgra[i + 1] = Blend(green, Math.Min(255, 255 * premultipliedBgra[i + 1] / a), a);
                    premultipliedBgra[i + 2] = Blend(red, Math.Min(255, 255 * premultipliedBgra[i + 2] / a), a);
                }
            }

            premultipliedBgra[i + 3] = 255;
        }
    }

    /// <summary>Composites straight-alpha BGRA pixels in place into opaque BGRA (no un-premultiply step).</summary>
    internal static void BlendStraightInPlace(Span<byte> bgra, byte red, byte green, byte blue)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a != 255)
            {
                if (a == 0)
                {
                    bgra[i] = blue;
                    bgra[i + 1] = green;
                    bgra[i + 2] = red;
                }
                else
                {
                    bgra[i] = Blend(blue, bgra[i], a);
                    bgra[i + 1] = Blend(green, bgra[i + 1], a);
                    bgra[i + 2] = Blend(red, bgra[i + 2], a);
                }
            }

            bgra[i + 3] = 255;
        }
    }

    /// <summary>Parses a 6-digit hex <c>RRGGBB</c> color; on failure returns false with all outputs 255.</summary>
    internal static bool TryParseBackground(string? hex, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 255;
        if (hex is null || hex.Length != 6)
        {
            return false;
        }

        for (int i = 0; i < 6; i++)
        {
            char c = hex[i];
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                return false;
            }
        }

        red = byte.Parse(hex.Substring(0, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        green = byte.Parse(hex.Substring(2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        blue = byte.Parse(hex.Substring(4, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return true;
    }

    private static byte Blend(int bg, int u, int a) => Div255((bg * (255 - a)) + (u * a));

    private static byte Div255(int v)
    {
        v += 128;
        return (byte)((v + (v >> 8)) >> 8);
    }
}
