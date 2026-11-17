using System;
using System.Text;

namespace OpenSlideSharp;

internal static unsafe class Utf8
{
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
