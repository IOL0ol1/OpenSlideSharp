namespace OpenSlideSharp.SkiaSharp.Internal;

/// <summary>Reads the few ICC header and tag table fields that decide how a profile is converted.</summary>
internal static class IccProfileInfo
{
    private const int HeaderSize = 128;
    private const int TagEntrySize = 12;

    private const uint Rgb = 0x52474220;     // 'RGB '
    private const uint Lab = 0x4C616220;     // 'Lab '
    private const uint A2B0 = 0x41324230;    // 'A2B0'
    private const uint LutAToB = 0x6D414220; // 'mAB '

    /// <summary>Whether the profile has a full header whose data color space (bytes 16 to 19) is <c>'RGB '</c>.</summary>
    public static bool HasRgbDataColorSpace(byte[] profile) =>
        profile.Length >= HeaderSize && ReadUInt32(profile, 16) == Rgb;

    /// <summary>
    /// Whether the PCS (bytes 20 to 23) is <c>'Lab '</c> and the A2B0 tag is a lutAtoBType (<c>'mAB '</c>). A missing
    /// A2B0 tag or a malformed tag table counts as no.
    /// </summary>
    public static bool IsLutAToBWithLabPcs(byte[] profile)
    {
        if (profile.Length < HeaderSize || ReadUInt32(profile, 20) != Lab)
        {
            return false;
        }

        long tag = FindTag(profile, A2B0);
        return tag >= 0 && ReadUInt32(profile, (int)tag) == LutAToB;
    }

    // The offset of the first tag with this signature, which has room for a type signature; -1 if there is no such
    // tag, or if the tag table or the tag runs past the end of the data.
    private static long FindTag(byte[] profile, uint signature)
    {
        if (profile.Length < HeaderSize + 4)
        {
            return -1;
        }

        long count = ReadUInt32(profile, HeaderSize);
        if (HeaderSize + 4 + (count * TagEntrySize) > profile.Length)
        {
            return -1;
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
            return size >= 4 && offset + size <= profile.Length ? offset : -1;
        }

        return -1;
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
