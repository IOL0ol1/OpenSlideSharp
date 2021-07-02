using System;
using System.Runtime.InteropServices;

namespace OpenSlideSharp.Interop
{

    /// <summary>
    /// The API for the OpenSlide library.
    /// OpenSlide is a C library that provides a simple interface to read
    /// whole-slide images(also known as virtual slides). See
    /// https://openslide.org/ for more details.
    /// </summary>
    /// <remarks>
    /// All functions except <see cref="Close(IntPtr)"/> are thread-safe.
    /// See the <see cref="Close(IntPtr)"/> documentation for its restrictions.
    /// </remarks>
    public static class NativeMethods
    {
        /// <summary>
        /// library name
        /// </summary>
        public const string LibraryName = "libopenslide-0";

        /// <summary>
        /// Quickly determine whether a whole slide image is recognized.
        /// </summary>
        /// <remarks>
        /// If OpenSlide recognizes the file referenced by <paramref name="filename"/>, 
        /// return a string identifying the slide format vendor.This is equivalent to the
        /// value of the <see cref="VENDOR"/> property. Calling
        /// <see cref="Open(string)"/> on this file will return a valid 
        /// OpenSlide object or an OpenSlide object in error state.
        ///
        /// Otherwise, return <see langword="null"/>.Calling <see cref="
        /// Open(string)"/> on this file will also
        /// return <see langword="null"/>.</remarks>
        /// <param name="filename">The filename to check. On Windows, this must be in UTF-8.</param>
        /// <returns>An identification of the format vendor for this file, or NULL.</returns>
        [DllImport(LibraryName, EntryPoint = "openslide_detect_vendor", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringMarshaler), MarshalCookie = "utf-8")]
        public extern static string DetectVendor([MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringMarshaler))] string filename);


        /// <summary>
        /// Open a whole slide image.
        /// </summary>
        /// <remarks>
        /// This function can be expensive; avoid calling it unnecessarily.  For 
        /// example, a tile server should not call <see cref="Open(string)"/>
        /// on every tile request.Instead, it should maintain a cache of OpenSlide 
        /// objects and reuse them when possible.
        /// </remarks>
        /// <param name="filename">The filename to open.  On Windows, this must be in UTF-8.</param>
        /// <returns>
        /// On success, a new OpenSlide object. 
        /// If the file is not recognized by OpenSlide, NULL. 
        /// If the file is recognized but an error occurred, an OpenSlide 
        /// object in error state.
        /// </returns>
        [DllImport(LibraryName, EntryPoint = "openslide_open", CallingConvention = CallingConvention.Cdecl)]
        public extern static IntPtr Open([MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringMarshaler))] string filename);


        ///<summary>
        ///Get the number of levels in the whole slide image.
        ///</summary>
        ///<param name="osr">The OpenSlide object.</param>
        ///<return>The number of levels, or -1 if an error occurred.</return> 
        [DllImport(LibraryName, EntryPoint = "openslide_get_level_count", CallingConvention = CallingConvention.Cdecl)]
        public extern static int GetLevelCount(IntPtr osr);


        ///<summary>
        ///Get the dimensions of level 0 (the largest level). Exactly
        ///equivalent to calling openslide_get_level_dimensions(osr, 0, w, h).
        ///</summary>
        ///<param name="osr">The OpenSlide object.</param> 
        ///<param name="w">The width of the image, or -1 if an error occurred.</param>
        ///<param name="h">The height of the image, or -1 if an error occurred.</param> 
        [DllImport(LibraryName, EntryPoint = "openslide_get_level0_dimensions", CallingConvention = CallingConvention.Cdecl)]
        public extern static void GetLevel0Dimensions(IntPtr osr, out long w, out long h);


        ///<summary>
        ///Get the dimensions of a level.
        ///</summary>
        ///<param name="osr">The OpenSlide object.</param> 
        ///<param name="level">The desired level.</param>
        ///<param name="w">The width of the image, or -1 if an error occurred or the level was out of range.</param>
        ///<param name="h">The height of the image, or -1 if an error occurred or the level was out of range.</param> 
        [DllImport(LibraryName, EntryPoint = "openslide_get_level_dimensions", CallingConvention = CallingConvention.Cdecl)]
        public extern static void GetLevelDimensions(IntPtr osr, int level, out long w, out long h);


        ///<summary>
        ///Get the downsampling factor of a given level.
        ///</summary>
        ///<param name="osr">The OpenSlide object.</param>
        ///<param name="level">The desired level.</param>
        ///<return>
        ///The downsampling factor for this level, or -1.0 if an error occurred
        ///or the level was out of range.
        ///</return> 
        [DllImport(LibraryName, EntryPoint = "openslide_get_level_downsample", CallingConvention = CallingConvention.Cdecl)]

        public extern static double GetLevelDownsample(IntPtr osr, int level);


        ///<summary>
        ///Get the best level to use for displaying the given downsample.
        ///</summary>
        ///<param name="osr">The OpenSlide object.</param> 
        ///<param name="downsample">The downsample factor.</param> 
        ///<return>
        ///The level identifier, or -1 if an error occurred.
        ///</return> 
        [DllImport(LibraryName, EntryPoint = "openslide_get_best_level_for_downsample", CallingConvention = CallingConvention.Cdecl)]

        public extern static int GetBestLevelForDownsample(IntPtr osr, double downsample);

        ///<summary>
        ///Copy pre-multiplied ARGB data from a whole slide image.
        ///</summary>
        ///<remarks>
        ///This function reads and decompresses a region of a whole slide
        ///image into the specified memory location.<paramref name="dest"/> 
        ///must be a valid pointer to enough memory to hold the region, at 
        ///least (<paramref name="w"/> * <paramref name="h"/> * 4) bytes 
        ///in length. If an error occurs or has occurred, then the memory
        ///pointed to by <paramref name="dest"/> will be cleared.
        ///</remarks>
        ///<param name="osr">The OpenSlide object.</param> 
        ///<param name="dest">The destination buffer for the ARGB data.</param> 
        ///<param name="x">The top left x-coordinate, in the level 0 reference frame.</param>
        ///<param name="y">The top left y-coordinate, in the level 0 reference frame.</param>
        ///<param name="level">The desired level.</param>
        ///<param name="w">The width of the region. Must be non-negative.</param> 
        ///<param name="h">The height of the region. Must be non-negative.</param> 
        ///
        [DllImport(LibraryName, EntryPoint = "openslide_read_region", CallingConvention = CallingConvention.Cdecl)]

        public extern static unsafe void ReadRegion(IntPtr osr, byte* dest, long x, long y, int level, long w, long h);

        ///<summary>
        ///Copy pre-multiplied ARGB data from a whole slide image.
        ///</summary>
        ///<remarks>
        ///This function reads and decompresses a region of a whole slide
        ///image into the specified memory location.<paramref name="dest"/> 
        ///must be a valid pointer to enough memory to hold the region, at 
        ///least (<paramref name="w"/> * <paramref name="h"/> * 4) bytes 
        ///in length. If an error occurs or has occurred, then the memory
        ///pointed to by <paramref name="dest"/> will be cleared.
        ///</remarks>
        ///<param name="osr">The OpenSlide object.</param> 
        ///<param name="dest">The destination buffer for the ARGB data.</param> 
        ///<param name="x">The top left x-coordinate, in the level 0 reference frame.</param>
        ///<param name="y">The top left y-coordinate, in the level 0 reference frame.</param>
        ///<param name="level">The desired level.</param>
        ///<param name="w">The width of the region. Must be non-negative.</param> 
        ///<param name="h">The height of the region. Must be non-negative.</param> 
        [DllImport(LibraryName, EntryPoint = "openslide_read_region", CallingConvention = CallingConvention.Cdecl)]
        public extern static unsafe void ReadRegion(IntPtr osr, byte[] dest, long x, long y, int level, long w, long h);

        ///<summary>
        ///Close an OpenSlide object.
        ///</summary>
        ///<remarks>
        ///No other threads may be using the object.
        ///After this call returns, the object cannot be used anymore.
        ///</remarks>
        ///<param name="osr">The OpenSlide object.</param>
        [DllImport(LibraryName, EntryPoint = "openslide_close", CallingConvention = CallingConvention.Cdecl)]
        public extern static void Close(IntPtr osr);


        /* 
         * A simple mechanism for detecting errors.
         * Sometimes an unrecoverable error can occur that will invalidate the
         * OpenSlide object. (This is typically something like an I/O error or
         * data corruption.)  When such an error happens in an OpenSlide
         * object, the object will move terminally into an error state.
         * 
         * While an object is in an error state, no OpenSlide functions will
         * have any effect on it except for openslide_close(). Functions
         * that are expected to return values will instead return an error
         * value, typically something like NULL or -1. openslide_read_region()
         * will clear its destination buffer instead of painting into
         * it. openslide_get_error() will return a non-NULL string containing
         * an error message. See the documentation for each function for
         * details on what is returned in case of error.
         * 
         * This style of error handling allows programs written in C to check
         * for errors only when convenient, because the error state is
         * terminal and the OpenSlide functions return harmlessly when there
         * has been an error.
         * 
         * If writing wrappers for OpenSlide in languages that support
         * exceptions, it is recommended that the error state be checked after
         * each call and converted into an exception for that language.
         */
        #region Error Handling.


        ///<summary>
        ///Get the current error string.
        ///</summary>
        ///<remarks>
        ///For a given OpenSlide object, once this function returns a non-NULL
        ///value, the only useful operation on the object is to call
        ///<see cref="Close(IntPtr)"/> to free its resources.
        ///</remarks>
        ///<param name="osr">The OpenSlide object.</param> 
        ///<return>
        ///A string describing the original error that caused
        ///the problem, or NULL if no error has occurred.
        ///</return> 
        [DllImport(LibraryName, EntryPoint = "openslide_get_error", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringMarshaler), MarshalCookie = "utf-8")]
        public extern static string GetError(IntPtr osr);

        #endregion

        /*
         * Some predefined properties.
         */
        #region Predefined Properties

        ///<summary>
        ///The name of the property containing a slide's comment, if any.
        ///</summary>
        public const string COMMENT = "openslide.comment";

        ///<summary>
        ///The name of the property containing an identification of the vendor.
        ///</summary>
        public const string VENDOR = "openslide.vendor";

        ///<summary>
        ///The name of the property containing the "quickhash-1" sum.
        ///</summary>
        public const string QUICKHASH1 = "openslide.quickhash-1";

        ///<summary>
        ///The name of the property containing a slide's background color, if any.
        ///It is represented as an RGB hex triplet.
        ///</summary>
        public const string BACKGROUND_COLOR = "openslide.background-color";

        ///<summary>
        ///The name of the property containing a slide's objective power, if known.
        ///</summary>
        public const string OBJECTIVE_POWER = "openslide.objective-power";

        ///<summary>
        ///The name of the property containing the number of microns per pixel in
        ///the X dimension of level 0, if known.
        ///</summary>
        public const string MPP_X = "openslide.mpp-x";

        ///<summary>
        ///The name of the property containing the number of microns per pixel in
        ///the Y dimension of level 0, if known.
        ///</summary>
        public const string MPP_Y = "openslide.mpp-y";

        ///<summary>
        ///The name of the property containing the X coordinate of the rectangle
        ///bounding the non-empty region of the slide, if available.
        ///</summary>
        public const string BOUNDS_X = "openslide.bounds-x";

        ///<summary>
        ///The name of the property containing the Y coordinate of the rectangle
        ///bounding the non-empty region of the slide, if available.
        ///</summary>
        public const string BOUNDS_Y = "openslide.bounds-y";

        ///<summary>
        ///The name of the property containing the width of the rectangle bounding
        ///the non-empty region of the slide, if available.
        ///</summary>
        public const string BOUNDS_WIDTH = "openslide.bounds-width";

        ///<summary>
        ///The name of the property containing the height of the rectangle bounding
        ///the non-empty region of the slide, if available.
        ///</summary>
        public const string BOUNDS_HEIGHT = "openslide.bounds-height";

        #endregion

        /* 
         * Querying properties.
         */
        #region Properties


        ///<summary>
        ///Get the NULL-terminated array of property names.
        ///</summary>
        ///<remarks>
        ///Certain vendor-specific metadata properties may exist
        ///within a whole slide image. They are encoded as key-value
        ///pairs. This call provides a list of names as strings
        ///that can be used to read properties with 
        ///<see cref="GetPropertyValue(IntPtr, string)"/>.
        ///</remarks>
        ///<param name="osr">The OpenSlide object.</param>
        ///<return> 
        ///A NULL-terminated string array of property names, or 
        ///an empty array if an error occurred.
        ///</return> 
        [DllImport(LibraryName, EntryPoint = "openslide_get_property_names", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringArrayMarshaler), MarshalCookie = "utf-8")]
        public extern static string[] GetPropertyNames(IntPtr osr);


        ///<summary>
        ///Get the value of a single property.
        ///</summary>
        ///<remarks>
        ///Certain vendor-specific metadata properties may exist
        ///within a whole slide image. They are encoded as key-value
        ///pairs. This call provides the value of the property given
        ///by <paramref name="name"/>.
        ///</remarks>
        ///<param name="osr">The OpenSlide object.</param>
        ///<param name="name">The name of the desired property. Must
        ///be a valid name as given by <see cref="GetPropertyNames(IntPtr)"/>.
        ///</param>
        ///<return>
        ///The value of the named property, or NULL if the property 
        ///doesn't exist or an error occurred.
        ///</return> 
        [DllImport(LibraryName, EntryPoint = "openslide_get_property_value", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringMarshaler), MarshalCookie = "utf-8")]
        public extern static string GetPropertyValue(IntPtr osr, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringMarshaler))] string name);

        #endregion


        /* 
         * Reading associated images.
         */
        #region Associated Images


        ///<summary>
        ///Get the NULL-terminated array of associated image names.
        ///</summary>
        ///<remarks>
        ///Certain vendor-specific associated images may exist
        ///within a whole slide image. They are encoded as key-value
        ///pairs. This call provides a list of names as strings
        ///that can be used to read associated images with
        ///<see cref="GetAssociatedImageDimensions
        ///(IntPtr, string, out long, out long)"/> and <see cref=
        ///"ReadAssociatedImage(IntPtr, string, byte[])"/>.
        ///</remarks>
        ///<param name="osr">The OpenSlide object.</param> 
        ///<return>
        ///A NULL-terminated string array of associated image names, or
        ///an empty array if an error occurred.
        ///</return> 
        [DllImport(LibraryName, EntryPoint = "openslide_get_associated_image_names", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringArrayMarshaler), MarshalCookie = "utf-8")]
        public extern static string[] GetAssociatedImageNames(IntPtr osr);

        ///<summary>
        ///Get the dimensions of an associated image.
        ///</summary>
        ///<remarks>
        ///This function returns the width and height of an associated image
        ///associated with a whole slide image. Once the dimensions are known,
        ///use <see cref="ReadAssociatedImage(IntPtr, string, byte[])"/>
        ///to read the image.
        ///</remarks>
        ///<param name="osr">The OpenSlide object.</param> 
        ///<param name="name">The name of the desired associated image. Must be
        ///a valid name as given by openslide_get_associated_image_names().</param> 
        ///<param name="w">The width of the associated image, or -1 if an error occurred.</param>
        ///<param name="h">The height of the associated image, or -1 if an error occurred.</param> 
        [DllImport(LibraryName, EntryPoint = "openslide_get_associated_image_dimensions", CallingConvention = CallingConvention.Cdecl)]
        public extern static void GetAssociatedImageDimensions(IntPtr osr, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringMarshaler))] string name, out long w, out long h);


        ///<summary>
        ///Copy pre-multiplied ARGB data from an associated image.
        ///</summary>
        ///<remarks>
        ///This function reads and decompresses an associated image associated
        ///with a whole slide image. <paramref name="dest"/> must be a valid 
        ///pointer to enough memory to hold the image, at least (width * 
        ///height * 4) bytes in length.  Get the width and height with
        ///<see cref="GetAssociatedImageDimensions(IntPtr, string,
        ///out long, out long)"/>. This call does nothing if an error occurred.
        ///</remarks>
        ///<param name="osr">The OpenSlide object.</param> 
        ///<param name="name">The name of the desired associated image. 
        ///Must be a valid name as given by 
        ///<see cref="GetAssociatedImageNames(IntPtr)"/>.
        ///</param> 
        ///<param name="dest">The destination buffer for the ARGB data.</param> 
        ///
        [DllImport(LibraryName, EntryPoint = "openslide_read_associated_image", CallingConvention = CallingConvention.Cdecl)]
        public extern static unsafe void ReadAssociatedImage(IntPtr osr, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringMarshaler))] string name, byte* dest);

        ///<summary>
        ///Copy pre-multiplied ARGB data from an associated image.
        ///</summary>
        ///<remarks>
        ///This function reads and decompresses an associated image associated
        ///with a whole slide image. <paramref name="dest"/> must be a valid 
        ///pointer to enough memory to hold the image, at least (width * 
        ///height * 4) bytes in length.  Get the width and height with
        ///<see cref="GetAssociatedImageDimensions(IntPtr, string,
        ///out long, out long)"/>. This call does nothing if an error occurred.
        ///</remarks>
        ///<param name="osr">The OpenSlide object.</param> 
        ///<param name="name">The name of the desired associated image. 
        ///Must be a valid name as given by 
        ///<see cref="GetAssociatedImageNames(IntPtr)"/>.
        ///</param> 
        ///<param name="dest">The destination buffer for the ARGB data.</param> 
        ///
        [DllImport(LibraryName, EntryPoint = "openslide_read_associated_image", CallingConvention = CallingConvention.Cdecl)]
        public extern static unsafe void ReadAssociatedImage(IntPtr osr, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringMarshaler))] string name, byte[] dest);

        #endregion


        /* 
         * Utility functions.
         */
        #region Miscellaneous

        ///<summary>
        ///Get the version of the OpenSlide library.
        ///</summary>
        ///<return>A string describing the OpenSlide version.</return> 
        [DllImport(LibraryName, EntryPoint = "openslide_get_version", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(CustomStringMarshaler))]
        public extern static string GetVersion();
        #endregion

    }
}
