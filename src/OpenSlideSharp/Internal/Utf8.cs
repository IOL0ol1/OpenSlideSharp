using System;
using System.Text;

namespace OpenSlideSharp;

internal static unsafe class Utf8
{
    /// <summary>Encodes <paramref name="value"/> as UTF-8 followed by a terminating NUL byte, for passing as a C string.</summary>
    /// <param name="value">The string to encode.</param>
    /// <param name="paramName">The public parameter <paramref name="value"/> came from, named in the exception.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> contains a NUL character, at which native code would silently truncate it.
    /// </exception>
    internal static byte[] ToNullTerminated(string value, string paramName)
    {
        if (value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                "The string contains a NUL character ('\\0'); OpenSlide would see only the text before it.", paramName);
        }

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
