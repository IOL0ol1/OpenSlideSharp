using System;
using System.Runtime.InteropServices;

namespace OpenSlideSharp.Interop
{
    public static unsafe partial class openslide
    {
        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_detect_vendor", ExactSpelling = true)]
        [return: NativeTypeName("const char *")]
        public static extern byte* detect_vendor([NativeTypeName("const char *")] byte* filename);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_open", ExactSpelling = true)]
        [return: NativeTypeName("openslide_t *")]
        public static extern IntPtr open([NativeTypeName("const char *")] byte* filename);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_get_level_count", ExactSpelling = true)]
        [return: NativeTypeName("int32_t")]
        public static extern int get_level_count([NativeTypeName("openslide_t *")] IntPtr osr);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_get_level0_dimensions", ExactSpelling = true)]
        public static extern void get_level0_dimensions([NativeTypeName("openslide_t *")] IntPtr osr, [NativeTypeName("int64_t *")] long* w, [NativeTypeName("int64_t *")] long* h);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_get_level_dimensions", ExactSpelling = true)]
        public static extern void get_level_dimensions([NativeTypeName("openslide_t *")] IntPtr osr, [NativeTypeName("int32_t")] int level, [NativeTypeName("int64_t *")] long* w, [NativeTypeName("int64_t *")] long* h);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_get_level_downsample", ExactSpelling = true)]
        public static extern double get_level_downsample([NativeTypeName("openslide_t *")] IntPtr osr, [NativeTypeName("int32_t")] int level);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_get_best_level_for_downsample", ExactSpelling = true)]
        [return: NativeTypeName("int32_t")]
        public static extern int get_best_level_for_downsample([NativeTypeName("openslide_t *")] IntPtr osr, double downsample);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_read_region", ExactSpelling = true)]
        public static extern void read_region([NativeTypeName("openslide_t *")] IntPtr osr, [NativeTypeName("uint32_t *")] uint* dest, [NativeTypeName("int64_t")] long x, [NativeTypeName("int64_t")] long y, [NativeTypeName("int32_t")] int level, [NativeTypeName("int64_t")] long w, [NativeTypeName("int64_t")] long h);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_get_icc_profile_size", ExactSpelling = true)]
        [return: NativeTypeName("int64_t")]
        public static extern long get_icc_profile_size([NativeTypeName("openslide_t *")] IntPtr osr);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_read_icc_profile", ExactSpelling = true)]
        public static extern void read_icc_profile([NativeTypeName("openslide_t *")] IntPtr osr, [NativeTypeName("void*")] IntPtr dest);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_close", ExactSpelling = true)]
        public static extern void close([NativeTypeName("openslide_t *")] IntPtr osr);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_get_error", ExactSpelling = true)]
        [return: NativeTypeName("const char *")]
        public static extern byte* get_error([NativeTypeName("openslide_t *")] IntPtr osr);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_get_property_names", ExactSpelling = true)]
        [return: NativeTypeName("const char *const *")]
        public static extern byte** get_property_names([NativeTypeName("openslide_t *")] IntPtr osr);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_get_property_value", ExactSpelling = true)]
        [return: NativeTypeName("const char *")]
        public static extern byte* get_property_value([NativeTypeName("openslide_t *")] IntPtr osr, [NativeTypeName("const char *")] byte* name);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_get_associated_image_names", ExactSpelling = true)]
        [return: NativeTypeName("const char *const *")]
        public static extern byte** get_associated_image_names([NativeTypeName("openslide_t *")] IntPtr osr);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_get_associated_image_dimensions", ExactSpelling = true)]
        public static extern void get_associated_image_dimensions([NativeTypeName("openslide_t *")] IntPtr osr, [NativeTypeName("const char *")] byte* name, [NativeTypeName("int64_t *")] long* w, [NativeTypeName("int64_t *")] long* h);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_read_associated_image", ExactSpelling = true)]
        public static extern void read_associated_image([NativeTypeName("openslide_t *")] IntPtr osr, [NativeTypeName("const char *")] byte* name, [NativeTypeName("uint32_t *")] uint* dest);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_get_associated_image_icc_profile_size", ExactSpelling = true)]
        [return: NativeTypeName("int64_t")]
        public static extern long get_associated_image_icc_profile_size([NativeTypeName("openslide_t *")] IntPtr osr, [NativeTypeName("const char *")] byte* name);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_read_associated_image_icc_profile", ExactSpelling = true)]
        public static extern void read_associated_image_icc_profile([NativeTypeName("openslide_t *")] IntPtr osr, [NativeTypeName("const char *")] byte* name, [NativeTypeName("void*")] IntPtr dest);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_cache_create", ExactSpelling = true)]
        [return: NativeTypeName("openslide_cache_t *")]
        public static extern IntPtr cache_create([NativeTypeName("size_t")] UIntPtr capacity);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_set_cache", ExactSpelling = true)]
        public static extern void set_cache([NativeTypeName("openslide_t *")] IntPtr osr, [NativeTypeName("openslide_cache_t *")] IntPtr cache);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_cache_release", ExactSpelling = true)]
        public static extern void cache_release([NativeTypeName("openslide_cache_t *")] IntPtr cache);

        [DllImport("libopenslide-1", CallingConvention = CallingConvention.Cdecl, EntryPoint = "openslide_get_version", ExactSpelling = true)]
        [return: NativeTypeName("const char *")]
        public static extern byte* get_version();

        [NativeTypeName("#define OPENSLIDE_PROPERTY_NAME_BACKGROUND_COLOR \"openslide.background-color\"")]
        public static ReadOnlySpan<byte> OPENSLIDE_PROPERTY_NAME_BACKGROUND_COLOR => new byte[] { 0x6F, 0x70, 0x65, 0x6E, 0x73, 0x6C, 0x69, 0x64, 0x65, 0x2E, 0x62, 0x61, 0x63, 0x6B, 0x67, 0x72, 0x6F, 0x75, 0x6E, 0x64, 0x2D, 0x63, 0x6F, 0x6C, 0x6F, 0x72, 0x00 };

        [NativeTypeName("#define OPENSLIDE_PROPERTY_NAME_BOUNDS_HEIGHT \"openslide.bounds-height\"")]
        public static ReadOnlySpan<byte> OPENSLIDE_PROPERTY_NAME_BOUNDS_HEIGHT => new byte[] { 0x6F, 0x70, 0x65, 0x6E, 0x73, 0x6C, 0x69, 0x64, 0x65, 0x2E, 0x62, 0x6F, 0x75, 0x6E, 0x64, 0x73, 0x2D, 0x68, 0x65, 0x69, 0x67, 0x68, 0x74, 0x00 };

        [NativeTypeName("#define OPENSLIDE_PROPERTY_NAME_BOUNDS_WIDTH \"openslide.bounds-width\"")]
        public static ReadOnlySpan<byte> OPENSLIDE_PROPERTY_NAME_BOUNDS_WIDTH => new byte[] { 0x6F, 0x70, 0x65, 0x6E, 0x73, 0x6C, 0x69, 0x64, 0x65, 0x2E, 0x62, 0x6F, 0x75, 0x6E, 0x64, 0x73, 0x2D, 0x77, 0x69, 0x64, 0x74, 0x68, 0x00 };

        [NativeTypeName("#define OPENSLIDE_PROPERTY_NAME_BOUNDS_X \"openslide.bounds-x\"")]
        public static ReadOnlySpan<byte> OPENSLIDE_PROPERTY_NAME_BOUNDS_X => new byte[] { 0x6F, 0x70, 0x65, 0x6E, 0x73, 0x6C, 0x69, 0x64, 0x65, 0x2E, 0x62, 0x6F, 0x75, 0x6E, 0x64, 0x73, 0x2D, 0x78, 0x00 };

        [NativeTypeName("#define OPENSLIDE_PROPERTY_NAME_BOUNDS_Y \"openslide.bounds-y\"")]
        public static ReadOnlySpan<byte> OPENSLIDE_PROPERTY_NAME_BOUNDS_Y => new byte[] { 0x6F, 0x70, 0x65, 0x6E, 0x73, 0x6C, 0x69, 0x64, 0x65, 0x2E, 0x62, 0x6F, 0x75, 0x6E, 0x64, 0x73, 0x2D, 0x79, 0x00 };

        [NativeTypeName("#define OPENSLIDE_PROPERTY_NAME_COMMENT \"openslide.comment\"")]
        public static ReadOnlySpan<byte> OPENSLIDE_PROPERTY_NAME_COMMENT => new byte[] { 0x6F, 0x70, 0x65, 0x6E, 0x73, 0x6C, 0x69, 0x64, 0x65, 0x2E, 0x63, 0x6F, 0x6D, 0x6D, 0x65, 0x6E, 0x74, 0x00 };

        [NativeTypeName("#define OPENSLIDE_PROPERTY_NAME_ICC_SIZE \"openslide.icc-size\"")]
        public static ReadOnlySpan<byte> OPENSLIDE_PROPERTY_NAME_ICC_SIZE => new byte[] { 0x6F, 0x70, 0x65, 0x6E, 0x73, 0x6C, 0x69, 0x64, 0x65, 0x2E, 0x69, 0x63, 0x63, 0x2D, 0x73, 0x69, 0x7A, 0x65, 0x00 };

        [NativeTypeName("#define OPENSLIDE_PROPERTY_NAME_MPP_X \"openslide.mpp-x\"")]
        public static ReadOnlySpan<byte> OPENSLIDE_PROPERTY_NAME_MPP_X => new byte[] { 0x6F, 0x70, 0x65, 0x6E, 0x73, 0x6C, 0x69, 0x64, 0x65, 0x2E, 0x6D, 0x70, 0x70, 0x2D, 0x78, 0x00 };

        [NativeTypeName("#define OPENSLIDE_PROPERTY_NAME_MPP_Y \"openslide.mpp-y\"")]
        public static ReadOnlySpan<byte> OPENSLIDE_PROPERTY_NAME_MPP_Y => new byte[] { 0x6F, 0x70, 0x65, 0x6E, 0x73, 0x6C, 0x69, 0x64, 0x65, 0x2E, 0x6D, 0x70, 0x70, 0x2D, 0x79, 0x00 };

        [NativeTypeName("#define OPENSLIDE_PROPERTY_NAME_OBJECTIVE_POWER \"openslide.objective-power\"")]
        public static ReadOnlySpan<byte> OPENSLIDE_PROPERTY_NAME_OBJECTIVE_POWER => new byte[] { 0x6F, 0x70, 0x65, 0x6E, 0x73, 0x6C, 0x69, 0x64, 0x65, 0x2E, 0x6F, 0x62, 0x6A, 0x65, 0x63, 0x74, 0x69, 0x76, 0x65, 0x2D, 0x70, 0x6F, 0x77, 0x65, 0x72, 0x00 };

        [NativeTypeName("#define OPENSLIDE_PROPERTY_NAME_QUICKHASH1 \"openslide.quickhash-1\"")]
        public static ReadOnlySpan<byte> OPENSLIDE_PROPERTY_NAME_QUICKHASH1 => new byte[] { 0x6F, 0x70, 0x65, 0x6E, 0x73, 0x6C, 0x69, 0x64, 0x65, 0x2E, 0x71, 0x75, 0x69, 0x63, 0x6B, 0x68, 0x61, 0x73, 0x68, 0x2D, 0x31, 0x00 };

        [NativeTypeName("#define OPENSLIDE_PROPERTY_NAME_VENDOR \"openslide.vendor\"")]
        public static ReadOnlySpan<byte> OPENSLIDE_PROPERTY_NAME_VENDOR => new byte[] { 0x6F, 0x70, 0x65, 0x6E, 0x73, 0x6C, 0x69, 0x64, 0x65, 0x2E, 0x76, 0x65, 0x6E, 0x64, 0x6F, 0x72, 0x00 };
    }
}
