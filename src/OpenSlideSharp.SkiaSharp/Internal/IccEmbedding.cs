using System;
using System.IO;

namespace OpenSlideSharp.SkiaSharp.Internal;

/// <summary>Inserts an ICC profile into already encoded JPEG and PNG data.</summary>
internal static class IccEmbedding
{
    private const int MaxJpegSegmentData = 65519; // 65535 - 2 (length) - 12 (ICC_PROFILE\0) - 2 (sequence)
    private const int MaxJpegSegments = 255;      // the sequence number and count are single bytes

    /// <summary>Whether a profile of this many bytes fits in the APP2 segments of a JPEG.</summary>
    public static bool FitsInJpeg(int profileLength) => profileLength <= MaxJpegSegments * MaxJpegSegmentData;

    public static byte[] EmbedJpeg(byte[] jpeg, byte[] profile)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new ArgumentException("Not a JPEG stream.", nameof(jpeg));
        }

        // Insert after SOI and after any leading APP0 (JFIF) segments.
        int pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF && jpeg[pos + 1] == 0xE0)
        {
            pos += 2 + ((jpeg[pos + 2] << 8) | jpeg[pos + 3]);
        }

        if (!FitsInJpeg(profile.Length))
        {
            throw new ArgumentException("ICC profile is too large to embed in a JPEG.", nameof(profile));
        }

        int count = Math.Max(1, (profile.Length + MaxJpegSegmentData - 1) / MaxJpegSegmentData);

        using var ms = new MemoryStream(jpeg.Length + profile.Length + count * 18);
        ms.Write(jpeg, 0, pos);
        byte[] tag = { (byte)'I', (byte)'C', (byte)'C', (byte)'_', (byte)'P', (byte)'R', (byte)'O', (byte)'F', (byte)'I', (byte)'L', (byte)'E', 0 };
        for (int i = 0; i < count; i++)
        {
            int offset = i * MaxJpegSegmentData;
            int length = Math.Min(MaxJpegSegmentData, profile.Length - offset);
            int segmentLength = length + 16;
            ms.WriteByte(0xFF);
            ms.WriteByte(0xE2);
            ms.WriteByte((byte)(segmentLength >> 8));
            ms.WriteByte((byte)segmentLength);
            ms.Write(tag, 0, tag.Length);
            ms.WriteByte((byte)(i + 1));
            ms.WriteByte((byte)count);
            ms.Write(profile, offset, length);
        }

        ms.Write(jpeg, pos, jpeg.Length - pos);
        return ms.ToArray();
    }

    public static byte[] EmbedPng(byte[] png, byte[] profile)
    {
        if (!PngChunks.HasSignature(png))
        {
            throw new ArgumentException("Not a PNG stream.", nameof(png));
        }

        using var ms = new MemoryStream(png.Length + profile.Length + 64);
        PngChunks.WriteSignature(ms);
        int pos = 8;
        bool inserted = false;
        while (pos + 12 <= png.Length)
        {
            int length = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            string type = new string(new[] { (char)png[pos + 4], (char)png[pos + 5], (char)png[pos + 6], (char)png[pos + 7] });
            int total = 12 + length;
            if (type == "iCCP" || type == "sRGB" || type == "gAMA" || type == "cHRM")
            {
                pos += total;
                continue;
            }

            ms.Write(png, pos, total);
            pos += total;
            if (!inserted && type == "IHDR")
            {
                PngChunks.WriteChunk(ms, "iCCP", PngChunks.BuildIccpData(profile));
                inserted = true;
            }
        }

        if (!inserted)
        {
            throw new ArgumentException("PNG has no IHDR chunk.", nameof(png));
        }

        return ms.ToArray();
    }
}
