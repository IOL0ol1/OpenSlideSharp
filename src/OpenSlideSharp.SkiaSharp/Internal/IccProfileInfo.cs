namespace OpenSlideSharp.SkiaSharp.Internal;

/// <summary>Reads the few ICC header and tag table fields that decide how a profile is converted.</summary>
internal static class IccProfileInfo
{
    private const int HeaderSize = 128;

    private const uint Rgb = 0x52474220;   // 'RGB '

    /// <summary>Whether the profile has a full header whose data color space (bytes 16 to 19) is <c>'RGB '</c>.</summary>
    public static bool HasRgbDataColorSpace(byte[] profile) =>
        profile.Length >= HeaderSize && ReadUInt32(profile, 16) == Rgb;

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
