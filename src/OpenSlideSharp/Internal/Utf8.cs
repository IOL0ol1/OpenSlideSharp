using System.Text;

namespace OpenSlideSharp;

internal static unsafe class Utf8
{
    /// <summary>Encodes <paramref name="value"/> as UTF-8 followed by a terminating NUL byte, for passing as a C string.</summary>
    internal static byte[] ToNullTerminated(string value)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, 0, value.Length, bytes, 0);
        return bytes;
    }

    /// <summary>Decodes a NUL-terminated UTF-8 string; returns null for a null pointer.</summary>
    internal static string? FromNative(byte* ptr)
    {
        if (ptr == null)
        {
            return null;
        }

        int length = 0;
        while (ptr[length] != 0)
        {
            length++;
        }

        return length == 0 ? string.Empty : Encoding.UTF8.GetString(ptr, length);
    }
}
