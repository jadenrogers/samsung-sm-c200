using System.CommandLine;
using Gear360.Core;
using Gear360.Core.Stitching;

namespace Gear360.Cli.Commands;

/// <summary><c>gear360 ffmpeg install|status</c>: downloads ffmpeg on request and shows which ffmpeg is used.</summary>
internal static class FfmpegCommand
{
    public static Command Create()
    {
        var force = new Option<bool>("--force") { Description = "Download again even if this version is already installed." };
        var install = new Command(
            "install",
            "Download a pinned ffmpeg build (with ffprobe) from its publisher into this app's data folder. Only needed when ffmpeg is not installed.")
        {
            force,
        };
        install.SetAction(async (parseResult, cancellationToken) => (await RunInstallAsync(parseResult.GetValue(force), cancellationToken)).ExitCode);

        var ffmpegPath = new Option<string?>("--ffmpeg")
        {
            Description = "Check this ffmpeg executable (or its folder) instead of searching.",
            HelpName = "path",
        };
        var status = new Command("status", "Show which ffmpeg would be used for stitching, its version, and the graphics card encoders that work with it.") { ffmpegPath };
        status.SetAction((parseResult, cancellationToken) => StatusAsync(parseResult.GetValue(ffmpegPath), cancellationToken));

        return new Command("ffmpeg", "Download ffmpeg (needed for stitching) or check which one is used.") { install, status };
    }

    /// <summary>The hint added to "ffmpeg not found" errors.</summary>
    public static string DownloadHint =>
        FfmpegManifest.ForCurrentPlatform() is { } package
            ? $"Or run 'gear360 ffmpeg install' to download ffmpeg {package.Version} (about {ByteSize.Format(package.TotalSize)}) from {package.Publisher}."
            : string.Empty;

    /// <summary>Downloads and installs the pinned build, printing progress; returns the exit code and the tools (null on failure).</summary>
    internal static async Task<(int ExitCode, FfmpegTools? Tools)> RunInstallAsync(bool force, CancellationToken cancellationToken)
    {
        var installer = new FfmpegInstaller();
        if (installer.Package is not { } package)
        {
            Console.Error.WriteLine(FfmpegManifest.UnsupportedMessage);
            return (ExitCodes.ToolMissing, null);
        }

        if (!force && installer.GetInstalledTools() is { } existing)
        {
            Console.WriteLine($"ffmpeg {package.Version} is already installed in {Path.GetDirectoryName(existing.FfmpegPath)}. Use --force to download it again.");
            return (ExitCodes.Success, existing);
        }

        Console.WriteLine($"Downloading ffmpeg {package.Version} for {package.RuntimeId} ({ByteSize.Format(package.TotalSize)}) from {package.Publisher}:");
        foreach (var download in package.Downloads)
        {
            Console.WriteLine($"  {download.Url}");
        }

        var interactive = !Console.IsOutputRedirected;
        var lastPercent = -1;
        var progress = new SynchronousProgress<(long Done, long? Total)>(p =>
        {
            if (p.Total is not > 0)
            {
                return;
            }

            var percent = (int)(p.Done * 100 / p.Total.Value);
            if (percent == lastPercent || (!interactive && percent % 10 != 0))
            {
                return;
            }

            lastPercent = percent;
            var text = $"  {percent,3}%  {ByteSize.Format(p.Done)} of {ByteSize.Format(p.Total.Value)}";
            if (interactive)
            {
                Console.Write("\r" + text);
            }
            else
            {
                Console.WriteLine(text);
            }
        });

        try
        {
            var tools = await installer.InstallAsync(progress, cancellationToken);
            if (interactive)
            {
                Console.WriteLine();
            }

            Console.WriteLine("Checked the SHA-256 of every download.");
            Console.WriteLine($"Installed ffmpeg and ffprobe in {Path.GetDirectoryName(tools.FfmpegPath)}");
            Console.WriteLine();
            Console.WriteLine(package.LicenseNote);
            Console.WriteLine();
            return (ExitCodes.Success, tools);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.Error.WriteLine("Cancelled. Nothing was installed.");
            return (ExitCodes.Cancelled, null);
        }
        catch (FfmpegInstallException ex)
        {
            Console.WriteLine();
            Console.Error.WriteLine(ex.Message);
            return (ExitCodes.Failed, null);
        }
    }

    private static async Task<int> StatusAsync(string? explicitPath, CancellationToken cancellationToken)
    {
        FfmpegTools tools;
        try
        {
            tools = FfmpegLocator.Locate(explicitPath);
        }
        catch (FfmpegNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            if (DownloadHint.Length > 0)
            {
                Console.Error.WriteLine(DownloadHint);
            }

            return ExitCodes.ToolMissing;
        }

        var downloaded = FfmpegLocator.IsDownloadedCopy(tools.FfmpegPath);
        Console.WriteLine($"ffmpeg:  {tools.FfmpegPath}{(downloaded ? "  (downloaded by gear360 ffmpeg install)" : string.Empty)}");
        Console.WriteLine($"ffprobe: {tools.FfprobePath}");
        var version = await FfmpegLocator.GetVersionAsync(tools.FfmpegPath, cancellationToken);
        if (version is null)
        {
            Console.Error.WriteLine("ffmpeg could not be run.");
            return ExitCodes.ToolMissing;
        }

        Console.WriteLine($"Version: {version}");

        // Each listed hardware encoder gets a short test encode, which takes a moment.
        var usable = new List<string>();
        foreach (var codec in Enum.GetValues<StitchCodec>())
        {
            foreach (var encoder in await EncoderDetector.Shared.ListUsableAsync(tools.FfmpegPath, codec, cancellationToken))
            {
                usable.Add(encoder.FfmpegName(codec));
            }
        }

        Console.WriteLine(usable.Count == 0
            ? "Hardware encoders: none usable; stitching encodes on the CPU."
            : $"Hardware encoders: {string.Join(", ", usable)}");
        var auto = await EncoderDetector.Shared.DetectAsync(tools.FfmpegPath, StitchCodec.H264, cancellationToken);
        Console.WriteLine($"--encoder auto uses: {StitchRunner.Describe(auto, StitchCodec.H264)} for H.264");
        return ExitCodes.Success;
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
