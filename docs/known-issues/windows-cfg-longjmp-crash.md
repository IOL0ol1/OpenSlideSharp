# Known issue: Windows process crash (0xC0000409) on corrupt JPEG/PNG data

**Status:** open. OpenSlideSharp does not ship a workaround; the proper fix belongs in openslide-bin.

## Symptom

On Windows, when OpenSlide hits corrupt JPEG or PNG data, the whole .NET process terminates with exit code `0xC0000409` (`STATUS_STACK_BUFFER_OVERRUN`, a fail-fast with `FAST_FAIL_INVALID_SET_OF_CONTEXT`). This applies to:

- a corrupt tile read through `ReadRegion`;
- a corrupt associated image;
- `OpenSlide.Open` itself, for formats that decode JPEG while opening (for example Hamamatsu).

The failure cannot be caught: there is no managed exception, `AppDomain.UnhandledException` does not fire, and nothing is logged.

**Security note:** anyone who can supply slide files to a Windows service built on OpenSlide can crash it (denial of service).

## Root cause

openslide-bin's `deps/setjmp.h` defines `setjmp(buf)` as `_setjmp(buf, NULL)` on Windows. It was introduced in [openslide-bin PR #49](https://github.com/openslide/openslide-bin/pull/49) to fix [issue #47](https://github.com/openslide/openslide-bin/issues/47). As a result, the Frame field of the jump buffer is 0.

The UCRT `longjmp` validates the jump buffer (`_except_validate_jump_buffer`, vcruntime `jbcxrval.c`). When Control Flow Guard (CFG) is enforced in the process, a buffer with `Frame == 0` triggers a fail-fast.

All .NET hosts we checked (the apphost and `dotnet.exe`, on .NET 8 and .NET 10) are built with CFG, so every .NET process is affected. `python.exe` is not built with CFG, which is why openslide-python does not crash.

Error paths that use `longjmp` and are therefore affected:

- libjpeg errors (OpenSlide's `error_exit` handler);
- libtiff's JPEG codec;
- libpng errors.

Not affected: OpenJPEG, libtiff's own error handlers, libxml2, sqlite, glib.

## Platforms

- Windows x64: confirmed.
- Linux and macOS: not affected as far as we know. The `setjmp` override applies only to Windows, and glibc and libSystem `longjmp` do no such validation. The corresponding tests run on Linux and macOS CI.
- Windows arm64: not verified.

## What does not help

Each of these was tried and does not prevent the crash:

- `<CETCompat>false</CETCompat>` on its own;
- running OpenSlide calls on a dedicated thread;
- launching with `dotnet app.dll` instead of the apphost;
- `DOTNET_LegacyExceptionHandling`;
- `NativeLibrary.Load` flags.

CFG cannot be disabled per call or at run time (`SetProcessMitigationPolicy` cannot turn it off).

## Options for application owners

**Recommended for services: process isolation.** Run slide decoding in worker processes and restart a worker when it exits. This also contains the denial-of-service risk.

**Not recommended (at your own risk): run the host without CFG and without CET shadow stacks.** In testing with a native host, the process survived only when both were off. This means clearing `GUARD_CF` in the host's PE header (or using an Exploit Protection override) and setting `<CETCompat>false</CETCompat>`. It weakens exploit mitigations in exactly the process that parses untrusted files. It also does not work for hosts you do not control, such as `dotnet.exe` or IIS `w3wp.exe`.

## Status and upstream

No workaround is shipped in OpenSlideSharp. A fix would pass a proper establisher frame to `_setjmp` or avoid the UCRT `longjmp` path, and has to be made in openslide-bin.

- [openslide-bin PR #49](https://github.com/openslide/openslide-bin/pull/49) (introduced the override)
- [openslide-bin issue #47](https://github.com/openslide/openslide-bin/issues/47) (the problem that PR addressed)
- [openslide issue #564](https://github.com/openslide/openslide/issues/564) (possibly the same issue: a .NET program crashing with 0xc0000409 in ucrtbase on a corrupt JPEG tile; it was closed as user error)

On Windows, the tests `ReadBadRegion_IsSticky` and `UnreadableAssociated_IsSticky` are skipped because they would trigger this crash.
