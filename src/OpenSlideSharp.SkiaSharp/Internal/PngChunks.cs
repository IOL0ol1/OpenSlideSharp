using System;
using System.IO;
using System.IO.Compression;

namespace OpenSlideSharp.SkiaSharp.Internal;

/// <summary>Minimal PNG chunk, CRC32 and zlib helpers (no dependency on newer BCL APIs).</summary>
internal static class PngChunks
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    public static uint Crc32(byte[] data, int offset, int count, uint crc = 0)
    {
        crc ^= 0xFFFFFFFFu;
        for (int i = offset; i < offset + count; i++)
        {
            crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    public static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte v in data)
        {
            a = (a + v) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    /// <summary>Wraps <paramref name="data"/> in a zlib stream (0x78 0x9C header, deflate, big-endian Adler-32).</summary>
    public static byte[] ZlibCompress(byte[] data, CompressionLevel level)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);
        using (var deflate = new DeflateStream(ms, level, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        uint adler = Adler32(data);
        ms.WriteByte((byte)(adler >> 24));
        ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));
        ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    public static void WriteSignature(Stream output) => output.Write(Signature, 0, Signature.Length);

    public static void WriteChunk(Stream output, string type, byte[] data, int dataOffset, int dataLength)
    {
        var header = new byte[8];
        WriteUInt32(header, 0, (uint)dataLength);
        for (int i = 0; i < 4; i++)
        {
            header[4 + i] = (byte)type[i];
        }

        output.Write(header, 0, 8);
        output.Write(data, dataOffset, dataLength);
        uint crc = Crc32(header, 4, 4);
        crc = Crc32(data, dataOffset, dataLength, crc);
        var tail = new byte[4];
        WriteUInt32(tail, 0, crc);
        output.Write(tail, 0, 4);
    }

    public static void WriteChunk(Stream output, string type, byte[] data) => WriteChunk(output, type, data, 0, data.Length);

    public static bool HasSignature(byte[] png)
    {
        if (png.Length < Signature.Length)
        {
            return false;
        }

        for (int i = 0; i < Signature.Length; i++)
        {
            if (png[i] != Signature[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds an 8-bit RGB PNG (color type 2) with an iCCP chunk, from opaque BGRA pixels.</summary>
    public static byte[] BuildRgbPngWithProfile(byte[] bgra, int width, int height, byte[] profile)
    {
        var raw = new byte[(long)height * (1 + width * 3)];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0; // filter: none
            int s = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                raw[o++] = bgra[s + 2];
                raw[o++] = bgra[s + 1];
                raw[o++] = bgra[s];
                s += 4;
            }
        }

        using var ms = new MemoryStream();
        WriteSignature(ms);
        var ihdr = new byte[13];
        WriteUInt32(ihdr, 0, (uint)width);
        WriteUInt32(ihdr, 4, (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "iCCP", BuildIccpData(profile));
        byte[] idat = ZlibCompress(raw, CompressionLevel.Fastest);
        WriteChunk(ms, "IDAT", idat);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>The payload of an iCCP chunk: name, NUL, compression method 0, zlib-compressed profile.</summary>
    public static byte[] BuildIccpData(byte[] profile)
    {
        byte[] compressed = ZlibCompress(profile, CompressionLevel.Optimal);
        const string name = "ICC Profile";
        var data = new byte[name.Length + 2 + compressed.Length];
        for (int i = 0; i < name.Length; i++)
        {
            data[i] = (byte)name[i];
        }

        data[name.Length] = 0;
        data[name.Length + 1] = 0;
        Buffer.BlockCopy(compressed, 0, data, name.Length + 2, compressed.Length);
        return data;
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
