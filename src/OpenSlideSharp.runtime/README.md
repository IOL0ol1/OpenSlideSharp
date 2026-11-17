# OpenSlideSharp.runtime

Native [OpenSlide](https://openslide.org/) 4.x libraries for [OpenSlideSharp](https://www.nuget.org/packages/OpenSlideSharp), taken from [openslide-bin](https://github.com/openslide/openslide-bin).

Supported runtime identifiers: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`.

Reference this package next to `OpenSlideSharp`; the native library is then copied to your output automatically.

## Licensing

This package is licensed under LGPL-2.1-only because it redistributes OpenSlide and its dependencies, each under its own license. See `licenses/` and `VERSIONS.md` inside the package for the exact components and versions. The corresponding source is the openslide-bin source tarball of the same version.
