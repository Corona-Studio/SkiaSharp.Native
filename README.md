# SkiaSharp.Native

MIT-licensed build scripts for reusable SkiaSharp 4.153.1 and HarfBuzzSharp
static archives. Native compilation runs in this public repository on standard
GitHub-hosted runners; consumers download release assets and perform their own
final NativeAOT link.

## Targets

Windows, macOS and Linux, each on x64 and arm64 (six native runners).
The mono/skia source revision and release tag are pinned in `native-lock.json`.
Run **Build static native libraries** manually to build all six targets. A
release is published only if every build succeeds. Release tags are never
reused: change `tag` and increment `rN` when changing flags or source revisions.

Each `skiasharp-native-<rid>.zip` contains `libSkiaSharp`, `libHarfBuzzSharp`,
all static dependency archives emitted by GN, `skiasharp.commit`, a manifest
with individual SHA-256 hashes, and upstream license notices. `SHA256SUMS`
contains the six asset hashes. These are static archives, not runtime DLLs.

## Build locally

Install .NET 10, Python 3, and the native compiler for your target host. Linux
requires clang, lld, build-essential, zlib1g-dev and libfontconfig1-dev.

```sh
dotnet run --project build/_build.csproj -- BuildNativeLibraries --target-os linux --target-arch x64
python3 build/package.py linux-x64
```

Build on the matching OS and architecture. Windows ARM64 selects the pinned
Windows x64 GN host tool, while compiling ARM64 libraries. Windows uses the
static MSVC runtime (`/MT`). macOS targets version 12.0 or later. Linux uses
Ubuntu 24.04; the consuming executable has the same libc compatibility floor.

## Consume

Pin a release tag and its archive hash. Verify the downloaded ZIP before
extracting to a clean directory, and match the managed SkiaSharp version and
source revision. Link **all** archives, not just the two C API archives. Unix
linkers may need an archive group or repeated libraries for circular references.
Windows also requires the platform system libraries; Linux requires fontconfig,
freetype and the C++ runtime; macOS requires the relevant system frameworks.
LauncherX retains its platform-specific linker configuration and rendering/text
smoke test in its own build.

The build scripts are MIT licensed. Skia and bundled dependencies retain their
own licenses, included in each release asset. No LauncherX application source,
private history, submodules, tokens, or build artifacts are part of this repo.
