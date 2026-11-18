# OpenSlideSharp.runtime

Native [OpenSlide](https://openslide.org/) 4.x libraries for [OpenSlideSharp](https://www.nuget.org/packages/OpenSlideSharp), taken from [openslide-bin](https://github.com/openslide/openslide-bin).

Supported runtime identifiers: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`.

Reference this package next to `OpenSlideSharp`; the native library is then copied to your output automatically.

## .NET Framework

OpenSlide ships Windows binaries for x64 only, so a .NET Framework app must run as a 64-bit process. Use `PlatformTarget` `x64`, or `AnyCPU` without `Prefer32Bit` (the SDK-style project default). An exe built as `x86`, or as `AnyCPU` with `Prefer32Bit` set to `true`, gets build warning `OPENSLIDE001` and cannot load the library at run time.

## Visual Studio C++

A C++ project (`vcxproj`) that installs the package with `packages.config` imports `build/native/OpenSlideSharp.runtime.targets`. For the `x64` platform it adds the OpenSlide headers and the `libopenslide.lib` import library, and copies `libopenslide-1.dll` to the output directory; other platforms fail the build with an error, as OpenSlide ships Windows binaries for x64 only. Native `PackageReference` in C++ projects (Visual Studio 2026 18.7 and later) is untested.

## Licensing

The native libraries are builds of OpenSlide (LGPL-2.1) from openslide-bin. Each platform's library statically links further components, each under its own license. `LICENSE.txt` in the package states the terms. `licenses/<platform>/` (`windows-x64`, `linux-x86_64`, `linux-aarch64`, `macos-arm64-x86_64`) holds the full license texts of that platform's components and `VERSIONS.md` with their versions. `openslide-bin.json` names the openslide-bin build and its source tarball (URL and sha256), which is the corresponding source. The same file, `openslide-bin-<binVersion>.tar.gz`, is attached to the [GitHub release](https://github.com/IOL0ol1/OpenSlideSharp/releases) of the package's version (tag `v<version>`), which keeps it after a nightly openslide-bin build is removed; check it against that sha256.

## Known issues

On Windows, corrupt JPEG or PNG data in a slide terminates the whole process with `0xC0000409`, and the failure cannot be caught. The cause is in the Windows build of the native OpenSlide library, and no workaround is shipped. Services that open untrusted slides on Windows should decode them in a separate worker process. See [the details](https://github.com/IOL0ol1/OpenSlideSharp/blob/master/docs/known-issues/windows-cfg-longjmp-crash.md).
