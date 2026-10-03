using System.Runtime.InteropServices;

namespace Gear360.Core.Stitching;

/// <summary>How a download is packed.</summary>
public enum FfmpegArchiveKind
{
    /// <summary>A zip archive.</summary>
    Zip,
}

/// <summary>One file to download for an ffmpeg package.</summary>
/// <param name="Url">A versioned https URL (never a "latest" link, so the hash stays valid).</param>
/// <param name="Sha256">The SHA-256 of the file, 64 lowercase hex characters.</param>
/// <param name="Size">The size in bytes, used for progress and the size shown before downloading.</param>
/// <param name="Kind">The archive type.</param>
/// <param name="Entries">
/// The archive entries to extract, mapped to the tool each one is: <c>"ffmpeg"</c> or <c>"ffprobe"</c>.
/// Every other entry is ignored.
/// </param>
public sealed record FfmpegDownload(string Url, string Sha256, long Size, FfmpegArchiveKind Kind, IReadOnlyDictionary<string, string> Entries);

/// <summary>A pinned ffmpeg build for one platform.</summary>
/// <param name="RuntimeId">The .NET runtime identifier it runs on, e.g. <c>win-x64</c>.</param>
/// <param name="Version">The ffmpeg version; also the install folder name.</param>
/// <param name="Publisher">Who builds and hosts the binaries.</param>
/// <param name="HomePage">The publisher's page for the builds.</param>
/// <param name="License">The license of the build.</param>
/// <param name="SourceUrl">Where the matching source code and build scripts are.</param>
/// <param name="Downloads">The files to download; together they hold ffmpeg and ffprobe.</param>
public sealed record FfmpegPackage(
    string RuntimeId,
    string Version,
    string Publisher,
    string HomePage,
    string License,
    string SourceUrl,
    IReadOnlyList<FfmpegDownload> Downloads)
{
    /// <summary>The total download size in bytes.</summary>
    public long TotalSize => Downloads.Sum(d => d.Size);

    /// <summary>A one-paragraph note on where the build comes from and its license.</summary>
    public string LicenseNote =>
        $"ffmpeg {Version} is downloaded from {Publisher} ({HomePage}). It is licensed under the {License}; " +
        $"source code: {SourceUrl} and https://ffmpeg.org/download.html. It is not part of Gear360Extractor and is not " +
        "redistributed by this project.";
}

/// <summary>The pinned ffmpeg builds that <see cref="FfmpegInstaller"/> can download.</summary>
/// <remarks>
/// Every URL points at a fixed version. Hashes were computed from the downloaded files and, where the publisher
/// offers them, checked against the publisher's own (GitHub asset digests, martin-riedl.de <c>.sha256</c> files).
/// To move to a newer build, change the URLs, sizes and hashes together.
/// </remarks>
public static class FfmpegManifest
{
    private const string GyanHome = "https://www.gyan.dev/ffmpeg/builds/";
    private const string RiedlHome = "https://ffmpeg.martin-riedl.de/";
    private const string RiedlSource = "https://git.martin-riedl.de/ffmpeg/build-script";
    private const string RiedlBase = "https://ffmpeg.martin-riedl.de/download/";

    /// <summary>Every pinned package.</summary>
    public static IReadOnlyList<FfmpegPackage> Packages { get; } =
    [
        new(
            "win-x64",
            "9.0.2",
            "Gyan Doshi (gyan.dev), via the GyanD/codexffmpeg GitHub releases",
            GyanHome,
            "GNU GPL v3",
            "https://github.com/GyanD/codexffmpeg",
            [
                new(
                    "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip",
                    "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba",
                    114_768_076,
                    FfmpegArchiveKind.Zip,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe"] = "ffmpeg",
                        ["ffmpeg-9.0.2-essentials_build/bin/ffprobe.exe"] = "ffprobe",
                    }),
            ]),
        Riedl(
            "osx-arm64",
            "macos/arm64/1789931890_9.0.2/",
            ("c8ed4c4e6978a03c485edbfe4e0a5dc2380f8a30bba5150531b31b094492d924", 28_395_699),
            ("fcbe839537485eaee7a7a8bc5cbc0f90d53617e80943e8a5b2e31cb851197ea6", 28_317_701)),
        Riedl(
            "osx-x64",
            "macos/amd64/1789931006_9.0.2/",
            ("7c6b4125b191cbf773832dc51f424cf2b6bb7da43007d1e066f95909e47cacd4", 33_816_391),
            ("2322438ed2f6319a691291b247d09c69dcaa3a982460d1f269a7e1af335cfdfd", 33_719_233)),
        Riedl(
            "linux-x64",
            "linux/amd64/1789931100_9.0.2/",
            ("fa8ecf4abbd290d98f7d188b8649cc6b391ae209a98452be955a15aab1909d7f", 33_326_445),
            ("3f428c49070be3d24ec338602b76d412e401ffcb8a5641ef0e729181a232fc32", 33_223_234)),
    ];

    /// <summary>The runtime identifier of this machine, e.g. <c>win-x64</c> or <c>osx-arm64</c>; null for other systems.</summary>
    public static string? CurrentRuntimeId
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win"
                : OperatingSystem.IsMacOS() ? "osx"
                : OperatingSystem.IsLinux() ? "linux"
                : null;
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => null,
            };
            return os is null || arch is null ? null : $"{os}-{arch}";
        }
    }

    /// <summary>The package for <paramref name="runtimeId"/>, or null when there is none.</summary>
    public static FfmpegPackage? Find(string? runtimeId) =>
        Packages.FirstOrDefault(p => string.Equals(p.RuntimeId, runtimeId, StringComparison.Ordinal));

    /// <summary>The package for this machine, or null when ffmpeg cannot be downloaded for it.</summary>
    public static FfmpegPackage? ForCurrentPlatform() => Find(CurrentRuntimeId);

    /// <summary>Why there is no download for this machine and what to do instead.</summary>
    public static string UnsupportedMessage =>
        $"There is no ffmpeg download for this system ({CurrentRuntimeId ?? RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture}). " +
        $"Supported: {string.Join(", ", Packages.Select(p => p.RuntimeId))}. {FfmpegLocator.InstallHint}";

    private static FfmpegPackage Riedl(string runtimeId, string folder, (string Sha256, long Size) ffmpeg, (string Sha256, long Size) ffprobe) =>
        new(
            runtimeId,
            "9.0.2",
            "Martin Riedl (ffmpeg.martin-riedl.de)",
            RiedlHome,
            "GNU GPL v3",
            RiedlSource,
            [
                new(RiedlBase + folder + "ffmpeg.zip", ffmpeg.Sha256, ffmpeg.Size, FfmpegArchiveKind.Zip, new Dictionary<string, string>(StringComparer.Ordinal) { ["ffmpeg"] = "ffmpeg" }),
                new(RiedlBase + folder + "ffprobe.zip", ffprobe.Sha256, ffprobe.Size, FfmpegArchiveKind.Zip, new Dictionary<string, string>(StringComparer.Ordinal) { ["ffprobe"] = "ffprobe" }),
            ]);
}
