using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

// ReSharper disable CheckNamespace
// ReSharper disable InconsistentNaming
partial class Build
{
    // SkiaSharp 4.153.1's exact mono/skia submodule revision. Keeping this in
    // lockstep with the managed package is required for a stable C ABI.
    const string SkiaCommit = "45afab4f1f0921f3feb97f58cf89b136fffa85e6";

    static AbsolutePath NativeCacheDirectory => RootDirectory / ".native";
    static AbsolutePath NativeDownloadsDirectory => NativeCacheDirectory / "downloads";
    static AbsolutePath SkiaSourceDirectory => NativeCacheDirectory / $"skia-{SkiaCommit}";
    AbsolutePath NativeLibrariesDirectory => RootDirectory / "artifacts" / "native" / RuntimeIdentifier;
    AbsolutePath NativeBuildDirectory => SkiaSourceDirectory / "out" / "launcherx" / RuntimeIdentifier;

    Target BuildNativeLibraries => d => d
        .OnlyWhenDynamic(() => UseNativeAot)
        .Executes(() =>
        {
            ValidateNativeHost();
            EnsureSkiaSource();

            var environment = CreateNativeEnvironment();
            var python = ResolvePython();
            Assert.NotNullOrEmpty(python,
                "A real Python 3 executable (not the Windows Store alias) is required by Skia GN scripts.");

            // git-sync-deps invokes fetch-gn unconditionally in a child process.
            // Patch that entry point before syncing, including on cache hits.
            if (TargetOs == "win" && TargetArch == "arm64")
                Run(python,
                    $"\"{RootDirectory / "build" / "patch-skia-gn.py"}\" \"{SkiaSourceDirectory}\"",
                    RootDirectory,
                    environment);

            // mono/skia m153 comments these out in DEPS despite the GN defaults.
            Run(python,
                $"\"{RootDirectory / "build" / "prepare-skia.py"}\" \"{SkiaSourceDirectory}\"",
                RootDirectory,
                environment);

            RunWithRetries(python, "tools/git-sync-deps", SkiaSourceDirectory, environment, 5);
            RunWithRetries(python, "bin/fetch-ninja", SkiaSourceDirectory, environment, 5);

            Directory.CreateDirectory(NativeBuildDirectory);
            File.WriteAllText(NativeBuildDirectory / "args.gn", CreateGnArguments());
            var gn = SkiaSourceDirectory / "bin" / (TargetOs == "win" ? "gn.exe" : "gn");
            if (!File.Exists(gn))
                RunWithRetries(python, "bin/fetch-gn", SkiaSourceDirectory, environment, 5);
            var ninja = SkiaSourceDirectory / "third_party" / "ninja" /
                        (TargetOs == "win" ? "ninja.exe" : "ninja");
            Assert.True(File.Exists(gn), $"Skia's pinned GN binary is missing: {gn}");
            Assert.True(File.Exists(ninja), $"Skia's pinned Ninja binary is missing: {ninja}");

            Run(gn,
                $"gen \"{NativeBuildDirectory}\" --script-executable=\"{python}\"",
                SkiaSourceDirectory,
                environment);
            Run(ninja, $"-C \"{NativeBuildDirectory}\" SkiaSharp HarfBuzzSharp", SkiaSourceDirectory, environment);

            CopyStaticLibraries();
        });

    void ValidateNativeHost()
    {
        var hostOs = DetectOs();
        var hostArch = DetectArch();
        Assert.True(hostOs == TargetOs,
            $"NativeAOT cannot cross-compile operating systems. {RuntimeIdentifier} requires a {TargetOs} runner, current host is {hostOs}-{hostArch}.");
        Assert.True(hostArch == TargetArch,
            $"The static Skia libraries must be built on a native {TargetArch} runner, current host is {hostOs}-{hostArch}.");
        Assert.True(TargetOs is "win" or "osx" or "linux", $"Unsupported target OS: {TargetOs}");
        Assert.True(TargetArch is "x64" or "arm64", $"Unsupported target architecture: {TargetArch}");
    }

    static void EnsureSkiaSource()
    {
        if (File.Exists(SkiaSourceDirectory / "BUILD.gn"))
            return;

        Directory.CreateDirectory(NativeDownloadsDirectory);
        var archive = NativeDownloadsDirectory / $"skia-{SkiaCommit}.zip";
        Download(
            $"https://github.com/mono/skia/archive/{SkiaCommit}.zip",
            archive);

        Log.Information("Extracting pinned mono/skia source {Commit}", SkiaCommit);
        ZipFile.ExtractToDirectory(archive, NativeCacheDirectory, true);
        Assert.True(File.Exists(SkiaSourceDirectory / "BUILD.gn"),
            $"Skia source archive did not contain the expected root: {SkiaSourceDirectory}");
    }

    static string ResolvePython()
    {
        foreach (var executable in new[] { "python3", "python" })
        {
            string path;
            try
            {
                path = ToolPathResolver.GetPathExecutable(executable);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(path) &&
                !path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                return path;
        }

        return null;
    }

    static void Download(string url, AbsolutePath destination)
    {
        var curl = ToolPathResolver.GetPathExecutable("curl")
                   ?? ToolPathResolver.GetPathExecutable("curl.exe");
        Assert.NotNullOrEmpty(curl, "curl is required to acquire pinned native sources.");
        Log.Information("Downloading {Url}", url);
        Run(curl,
            $"--silent --show-error --fail --location --retry 5 --retry-all-errors --connect-timeout 30 --continue-at - --output \"{destination}\" \"{url}\"",
            RootDirectory);
        Log.Information("Downloaded {Path} ({Bytes:N0} bytes)", destination, new FileInfo(destination).Length);
    }

    Dictionary<string, string> CreateNativeEnvironment()
    {
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(x => (string)x.Key, x => x.Value?.ToString() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        if (TargetOs == "win")
        {
            // Applies to every parallel git-sync-deps child without changing the
            // developer's global Git configuration.
            environment["GIT_CONFIG_COUNT"] = "3";
            environment["GIT_CONFIG_KEY_0"] = "http.sslBackend";
            environment["GIT_CONFIG_VALUE_0"] = "openssl";
            environment["GIT_CONFIG_KEY_1"] = "http.version";
            environment["GIT_CONFIG_VALUE_1"] = "HTTP/1.1";
            // HarfBuzz test font paths exceed MAX_PATH beneath the Skia cache.
            environment["GIT_CONFIG_KEY_2"] = "core.longpaths";
            environment["GIT_CONFIG_VALUE_2"] = "true";
        }
        return environment;
    }

    string CreateGnArguments()
    {
        var targetOs = TargetOs switch
        {
            "win" => "win",
            "osx" => "mac",
            _ => "linux"
        };
        var flags = TargetOs switch
        {
            // Put every function and global in its own COMDAT. NativeAOT's
            // final /OPT:REF /OPT:ICF link can then discard unused Skia code
            // and fold duplicate data instead of retaining whole object files.
            "win" => "[\"/MT\", \"/EHsc\", \"/guard:cf\", \"/Gy\", \"/Gw\", \"-D_HAS_AUTO_PTR_ETC=1\", \"-DSKIA_C_DLL\", \"-DSK_AVOID_SLOW_RASTER_PIPELINE_BLURS\", \"-DSK_ENABLE_LEGACY_SHADERCONTEXT\"]",
            // Keep Expat's entropy selection aligned with SkiaSharp's m153
            // Linux build. Ubuntu 24.04 provides the syscall on both supported
            // architectures; XML_DEV_URANDOM enables Expat's entropy code.
            "linux" => "[\"-fPIC\", \"-ffunction-sections\", \"-fdata-sections\", \"-DSKIA_C_DLL\", \"-DHAVE_SYSCALL_GETRANDOM\", \"-DXML_DEV_URANDOM\", \"-DSK_AVOID_SLOW_RASTER_PIPELINE_BLURS\", \"-DSK_ENABLE_LEGACY_SHADERCONTEXT\"]",
            _ => "[\"-ffunction-sections\", \"-fdata-sections\", \"-DSKIA_C_DLL\", \"-DHAVE_ARC4RANDOM_BUF\", \"-DSK_AVOID_SLOW_RASTER_PIPELINE_BLURS\", \"-DSK_ENABLE_LEGACY_SHADERCONTEXT\"]"
        };

        var lines = new List<string>
        {
            $"target_os=\"{targetOs}\"",
            $"target_cpu=\"{TargetArch}\"",
            "is_debug=false",
            "is_official_build=true",
            "is_component_build=false",
            "is_static_skiasharp=true",
            "skia_enable_tools=false",
            "skia_enable_ganesh=true",
            "skia_enable_graphite=false",
            "skia_enable_skottie=true",
            "skia_enable_optimize_size=true",
            "skia_use_gl=true",
            "skia_use_vulkan=false",
            "skia_use_direct3d=false",
            "skia_use_metal=false",
            "skia_use_harfbuzz=false",
            "skia_use_icu=false",
            "skia_use_partition_alloc=false",
            "skia_use_piex=true",
            "skia_use_system_expat=false",
            "skia_use_system_libjpeg_turbo=false",
            "skia_use_system_libpng=false",
            "skia_use_system_libwebp=false",
            "skia_use_system_zlib=false",
            $"extra_cflags={flags}",
            $"extra_cflags_cc={flags}"
        };

        if (TargetOs == "linux")
            lines.Add("skia_use_x11=false");

        if (TargetOs == "osx")
            lines.Add("min_macos_version=\"12.0\"");

        var llvmDirectory = @"C:\Program Files\LLVM";
        if (TargetOs == "win" && Directory.Exists(llvmDirectory))
            lines.Add($"clang_win=\"{llvmDirectory.Replace("\\", "/")}\"");

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    void CopyStaticLibraries()
    {
        var extension = TargetOs == "win" ? ".lib" : ".a";
        var libraries = Directory
            .EnumerateFiles(NativeBuildDirectory, $"*{extension}", SearchOption.AllDirectories)
            .ToArray();

        // Like Avalonia's SkiaBuilder, retain every archive emitted by GN. The
        // SkiaSharp archive contains the C API and core objects, while codecs,
        // SkSL, Skottie and platform font managers remain separate archives.
        Assert.True(libraries.Length > 2,
            $"Skia build produced an incomplete static library set in {NativeBuildDirectory}");

        Directory.CreateDirectory(NativeLibrariesDirectory);
        foreach (var stale in Directory.EnumerateFiles(NativeLibrariesDirectory, $"*{extension}"))
            File.Delete(stale);

        foreach (var source in libraries)
        {
            var sourceName = Path.GetFileName(source);
            var destinationName = TargetOs == "win" &&
                                  (sourceName.Equals("SkiaSharp.lib", StringComparison.OrdinalIgnoreCase) ||
                                   sourceName.Equals("HarfBuzzSharp.lib", StringComparison.OrdinalIgnoreCase))
                ? $"lib{sourceName}"
                : sourceName;
            var destination = NativeLibrariesDirectory / destinationName;

            Assert.False(File.Exists(destination),
                $"Duplicate static library name while flattening Skia output: {destinationName}");
            File.Copy(source, destination);
            Assert.True(new FileInfo(destination).Length > 0, $"Static library is empty: {destination}");
        }

        Assert.True(File.Exists(NativeLibrariesDirectory / $"libSkiaSharp{extension}"),
            "The native build did not produce the SkiaSharp C API archive.");
        Assert.True(File.Exists(NativeLibrariesDirectory / $"libHarfBuzzSharp{extension}"),
            "The native build did not produce the HarfBuzzSharp C API archive.");
        File.WriteAllText(NativeLibrariesDirectory / "skiasharp.commit", SkiaCommit + Environment.NewLine);
        Log.Information("Prepared {Count} static Skia/HarfBuzz archives in {Directory}",
            libraries.Length,
            NativeLibrariesDirectory);
    }

    static void Run(
        string tool,
        string arguments,
        AbsolutePath workingDirectory,
        IReadOnlyDictionary<string, string> environment = null)
    {
        ProcessTasks.StartProcess(tool, arguments, workingDirectory: workingDirectory, environmentVariables: environment)
            .AssertZeroExitCode();
    }

    static void RunWithRetries(
        string tool,
        string arguments,
        AbsolutePath workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        int attempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Run(tool, arguments, workingDirectory, environment);
                return;
            }
            catch when (attempt < attempts)
            {
                var delay = TimeSpan.FromSeconds(attempt * 3);
                Log.Warning("Native dependency sync attempt {Attempt} failed; retrying in {Delay}", attempt, delay);
                Thread.Sleep(delay);
            }
        }
    }
}
