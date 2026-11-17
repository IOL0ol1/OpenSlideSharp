# OpenSlideSharp.runtime

Native [OpenSlide](https://openslide.org/) 4.x libraries for [OpenSlideSharp](https://www.nuget.org/packages/OpenSlideSharp), taken from [openslide-bin](https://github.com/openslide/openslide-bin).

Supported runtime identifiers: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`.

Reference this package next to `OpenSlideSharp`; the native library is then copied to your output automatically.

## .NET Framework

OpenSlide ships Windows binaries for x64 only, so a .NET Framework app must run as a 64-bit process. Use `PlatformTarget` `x64`, or `AnyCPU` without `Prefer32Bit` (the SDK-style project default). An exe built as `x86`, or as `AnyCPU` with `Prefer32Bit` set to `true`, gets build warning `OPENSLIDE001` and cannot load the library at run time.

## Licensing

This package is licensed under LGPL-2.1-only because it redistributes OpenSlide and its dependencies, each under its own license. See `licenses/` and `VERSIONS.md` inside the package for the exact components and versions. The corresponding source is the openslide-bin source tarball of the same version.

## Known issues

On Windows, corrupt JPEG or PNG data in a slide terminates the whole process with `0xC0000409`, and the failure cannot be caught. The cause is in the Windows build of the native OpenSlide library, and no workaround is shipped. Services that open untrusted slides on Windows should decode them in a separate worker process. See [the details](https://github.com/IOL0ol1/OpenSlideSharp/blob/master/docs/known-issues/windows-cfg-longjmp-crash.md).
