# OpenSlide: source code and replacing the library

The viewer contains a build of [OpenSlide](https://openslide.org/) from
[openslide-bin](https://github.com/openslide/openslide-bin): `libopenslide-1.dll` on Windows, `libopenslide-1.so` on
Linux and `libopenslide-1.dylib` on macOS, which the executable extracts when it first starts. OpenSlide is licensed
under the GNU Lesser General Public License 2.1, and the libraries linked into it under their own licenses: see
`openslide-bin/<platform>/`, and `openslide-bin/<platform>/VERSIONS.md` for their versions.
`openslide-bin/openslide-bin.json` names the openslide-bin build and its source tarball.

## Source code

The corresponding source code is the openslide-bin source archive of the same build, `openslide-bin-<version>.tar.gz`,
published with its binaries:

- a release: `https://github.com/openslide/openslide-bin/releases/download/v<version>/openslide-bin-<version>.tar.gz`
- a nightly build: the release `v<version>` on <https://github.com/openslide/builds/releases> (nightly builds are kept
  for a limited time)

The viewer has the version of the OpenSlideSharp packages it was built with, and the table under "Versioning" in
<https://github.com/IOL0ol1/OpenSlideSharp#versioning> gives the openslide-bin build of each version.

## Using another OpenSlide build

Set the `OPENSLIDE_LIBRARY_PATH` environment variable to the full path of another build of the OpenSlide 4.x library,
for example one built from the source above, changed or not. The viewer then loads that library instead of the one it
contains:

```sh
OPENSLIDE_LIBRARY_PATH=/path/to/libopenslide.so.1 ./OpenSlideSharp.Viewer slide.svs
```

```powershell
$env:OPENSLIDE_LIBRARY_PATH = 'C:\path\to\libopenslide-1.dll'; .\OpenSlideSharp.Viewer.exe slide.svs
```

`OpenSlideSharp.Viewer --check slide.svs` prints the version of the library it loaded (`openslide=...`).
