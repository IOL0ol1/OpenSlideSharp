using System;

namespace OpenSlideSharp;

/// <summary>
/// An error reported by OpenSlide. Once a slide has reported an error it stays in that error state,
/// and every later operation on it throws this exception again.
/// </summary>
public class OpenSlideException : Exception
{
    /// <summary>Initializes a new instance with the specified error message.</summary>
    /// <param name="message">The error message, usually the text returned by <c>openslide_get_error</c>.</param>
    public OpenSlideException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified error message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="inner">The exception that caused this one.</param>
    public OpenSlideException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>OpenSlide does not recognize the file as a supported slide format, or the file does not exist.</summary>
public class OpenSlideUnsupportedFormatException : OpenSlideException
{
    /// <summary>Initializes a new instance with the specified error message.</summary>
    /// <param name="message">The error message.</param>
    public OpenSlideUnsupportedFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified error message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="inner">The exception that caused this one.</param>
    public OpenSlideUnsupportedFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
