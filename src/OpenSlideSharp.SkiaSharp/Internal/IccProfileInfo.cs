namespace OpenSlideSharp.SkiaSharp.Internal;

/// <summary>Reads the few ICC header and tag table fields that decide how a profile is converted.</summary>
internal static class IccProfileInfo
{
    private const int HeaderSize = 128;
    private const int TagEntrySize = 12;
    private const long Absent = -1;
    private const long Malformed = -2;

    private const uint Rgb = 0x52474220;   // 'RGB '
    private const uint A2B0 = 0x41324230;  // 'A2B0'
    private const uint A2B1 = 0x41324231;  // 'A2B1'
    private const uint Lut16 = 0x6D667432; // 'mft2'

    /// <summary>Whether the profile has a full header whose data color space (bytes 16 to 19) is <c>'RGB '</c>.</summary>
    public static bool HasRgbDataColorSpace(byte[] profile) =>
        profile.Length >= HeaderSize && ReadUInt32(profile, 16) == Rgb;

    /// <summary>
    /// Whether the A2B table that skcms uses (A2B0, or A2B1 if there is no A2B0) is a lut16Type (<c>'mft2'</c>).
    /// A malformed tag table counts as no.
    /// </summary>
    public static bool HasLut16A2B(byte[] profile)
    {
        long tag = FindTag(profile, A2B0);
        if (tag == Absent)
        {
            tag = FindTag(profile, A2B1);
        }

        return tag >= 0 && ReadUInt32(profile, (int)tag) == Lut16;
    }

    // The offset of the first tag with this signature, which has room for a type signature; Absent if there is no
    // such tag, Malformed if the tag table or the tag runs past the end of the data.
    private static long FindTag(byte[] profile, uint signature)
    {
        if (profile.Length < HeaderSize + 4)
        {
            return Malformed;
        }

        long count = ReadUInt32(profile, HeaderSize);
        if (HeaderSize + 4 + (count * TagEntrySize) > profile.Length)
        {
            return Malformed;
        }

        for (long i = 0; i < count; i++)
        {
            int entry = (int)(HeaderSize + 4 + (i * TagEntrySize));
            if (ReadUInt32(profile, entry) != signature)
            {
                continue;
            }

            long offset = ReadUInt32(profile, entry + 4);
            long size = ReadUInt32(profile, entry + 8);
            return size >= 4 && offset + size <= profile.Length ? offset : Malformed;
        }

        return Absent;
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
