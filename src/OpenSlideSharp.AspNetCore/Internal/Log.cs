using System;
using Microsoft.Extensions.Logging;

namespace OpenSlideSharp.AspNetCore.Internal;

internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Debug, "Cannot open slide {FilePath}.")]
    public static partial void CannotOpenSlide(ILogger logger, string filePath, Exception exception);

    [LoggerMessage(
        2,
        LogLevel.Warning,
        "{FilePath}: the ICC profile of {Image} cannot be converted to sRGB. Deep Zoom tiles embed the profile instead, and OME-Zarr chunks keep the unconverted pixels.")]
    public static partial void CannotConvertProfile(ILogger logger, string filePath, string image);

    [LoggerMessage(
        3,
        LogLevel.Warning,
        "{FilePath}: {Image} has an ICC profile, which OME-Zarr cannot embed. OME-Zarr chunks keep the unconverted pixels and drop the profile.")]
    public static partial void OmeZarrCannotEmbedProfile(ILogger logger, string filePath, string image);

    [LoggerMessage(4, LogLevel.Error, "{FilePath}: OpenSlide reported an error while serving {Request}; the slide is evicted from the cache.")]
    public static partial void SlideError(ILogger logger, string filePath, string request, Exception exception);
}
