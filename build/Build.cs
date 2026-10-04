using System;
using System.Runtime.InteropServices;
using Nuke.Common;

partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.BuildNativeLibraries);
    [Parameter("Target operating system: win, osx, linux")]
    readonly string TargetOs = DetectOs();
    [Parameter("Target architecture: x64, arm64")]
    readonly string TargetArch = DetectArch();
    string RuntimeIdentifier => $"{TargetOs}-{TargetArch}";
    bool UseNativeAot => true;
    static string DetectOs() => OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
    static string DetectArch() => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
}
