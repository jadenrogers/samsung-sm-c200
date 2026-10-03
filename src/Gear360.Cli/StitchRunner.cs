using Gear360.Core;
using Gear360.Core.Stitching;

namespace Gear360.Cli;

/// <summary>Stitches a list of files one after another, printing one progress line per file.</summary>
internal static class StitchRunner
{
    /// <summary>Finds ffmpeg, printing why and how to install it when it is missing.</summary>
    /// <param name="ffmpegPath">The <c>--ffmpeg</c> value, or null to search.</param>
    /// <param name="download">
    /// <c>--get-ffmpeg</c> was given: when nothing is found (and no <paramref name="ffmpegPath"/> was given), download
    /// the pinned build first.
    /// </param>
    /// <param name="cancellationToken">Cancels the download.</param>
    public static async Task<(FfmpegTools? Tools, int ExitCode)> LocateToolsAsync(string? ffmpegPath, bool download, CancellationToken cancellationToken)
    {
        try
        {
            return (FfmpegLocator.Locate(ffmpegPath), ExitCodes.Success);
        }
        catch (FfmpegNotFoundException ex)
        {
            var canDownload = string.IsNullOrWhiteSpace(ffmpegPath) && FfmpegManifest.ForCurrentPlatform() is not null;
            if (download && canDownload)
            {
                Console.WriteLine("ffmpeg was not found; downloading it because --get-ffmpeg was given.");
                var (exitCode, tools) = await Commands.FfmpegCommand.RunInstallAsync(force: false, cancellationToken);
                return (tools, tools is null ? (exitCode == ExitCodes.Cancelled ? exitCode : ExitCodes.ToolMissing) : ExitCodes.Success);
            }

            Console.Error.WriteLine(ex.Message);
            if (canDownload)
            {
                Console.Error.WriteLine(Commands.FfmpegCommand.DownloadHint + " Or add --get-ffmpeg to download it first.");
            }

            return (null, ExitCodes.ToolMissing);
        }
    }

    /// <summary>
    /// Works out which encoder <c>--encoder</c> means on this computer and prints it. Done before copying or stitching
    /// anything so that an unusable explicit choice is reported straight away.
    /// </summary>
    /// <returns>The encoder, or null (after printing why) when the chosen hardware encoder does not work.</returns>
    public static async Task<StitchEncoder?> ResolveEncoderAsync(FfmpegTools tools, StitchOptions options, StitchProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var encoder = await new Stitcher(tools).ResolveEncoderAsync(options, cancellationToken);
            Console.WriteLine($"Encoder: {Describe(encoder, options.Codec)}" +
                (options.Encoder == StitchEncoder.Auto && encoder == StitchEncoder.Cpu ? " (no working graphics card encoder found)" : string.Empty));
            Console.WriteLine(SettingsLine(profile, options, encoder));
            return encoder;
        }
        catch (StitchException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return null;
        }
    }

    /// <summary>"Quality high: h264_nvenc, CQ 21 (CRF 16), preset p7, HQ tuning, interp line, audio copy, FOV 195".</summary>
    internal static string SettingsLine(StitchProfile profile, StitchOptions options, StitchEncoder encoder) =>
        $"Quality {profile.CliName()}{(StitchProfiles.ApplyTo(options, profile) == options ? string.Empty : " with overrides")}: " +
        StitchProfiles.Summarize(options, encoder);

    /// <summary>"NVIDIA NVENC (h264_nvenc)".</summary>
    public static string Describe(StitchEncoder encoder, StitchCodec codec) => $"{encoder.DisplayName()} ({encoder.FfmpegName(codec)})";

    /// <summary>Stitches each file; returns the number that failed.</summary>
    /// <param name="tools">ffmpeg and ffprobe.</param>
    /// <param name="files">The files to stitch.</param>
    /// <param name="options">Stitch settings.</param>
    /// <param name="encoder">The encoder from <see cref="ResolveEncoderAsync"/>, shown on each video's line.</param>
    /// <param name="cancellationToken">Stops the running ffmpeg.</param>
    /// <exception cref="OperationCanceledException">Cancelled; the file being stitched was removed.</exception>
    public static async Task<int> RunAsync(FfmpegTools tools, IReadOnlyList<string> files, StitchOptions options, StitchEncoder encoder, CancellationToken cancellationToken)
    {
        var stitcher = new Stitcher(tools);
        int stitched = 0, skipped = 0, failed = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var line = new ConsoleLine();
            var encoderText = MediaKinds.FromPath(file) == MediaKind.Video ? $" with {encoder.DisplayName()}" : string.Empty;
            var prefix = $"[{i + 1}/{files.Count}] Stitching {Path.GetFileName(file)}{encoderText}";
            line.Update($"{prefix}...");
            var lastPercent = -1;
            var progress = new SynchronousProgress<double>(fraction =>
            {
                var percent = (int)(fraction * 100);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    line.Update($"{prefix} {percent,3}%");
                }
            });

            try
            {
                var result = await stitcher.StitchAsync(file, options, progress, cancellationToken);
                if (result.Skipped)
                {
                    skipped++;
                    line.Finish($"{prefix}: skipped, {result.OutputPath} already exists (use --overwrite to replace it)");
                }
                else
                {
                    stitched++;
                    var tuning = result.TuningSkipped ? " (this graphics card does not support HQ tuning; standard settings used)" : string.Empty;
                    line.Finish(result.FallbackReason is { } reason
                        ? $"{prefix}: done on the CPU instead, because {reason} -> {result.OutputPath}"
                        : $"{prefix}: done{tuning} -> {result.OutputPath}");
                }
            }
            catch (OperationCanceledException)
            {
                line.Finish($"{prefix}: cancelled");
                throw;
            }
            catch (Exception ex) when (ex is StitchException or IOException or UnauthorizedAccessException)
            {
                failed++;
                line.Finish($"{prefix}: FAILED: {ex.Message}");
            }
        }

        Console.WriteLine($"Stitching done: {stitched} stitched, {skipped} skipped, {failed} failed.");
        return failed;
    }

    /// <summary>An <see cref="IProgress{T}"/> that runs its handler on the reporting thread, so lines stay in order.</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            lock (handler)
            {
                handler(value);
            }
        }
    }

    /// <summary>A console line rewritten in place when output goes to a terminal.</summary>
    private sealed class ConsoleLine
    {
        private readonly bool _interactive = !Console.IsOutputRedirected;
        private int _length;

        public void Update(string text)
        {
            if (!_interactive)
            {
                return;
            }

            var padding = Math.Max(0, _length - text.Length);
            Console.Write("\r" + text + new string(' ', padding));
            _length = text.Length;
        }

        public void Finish(string text)
        {
            if (_interactive && _length > 0)
            {
                Update(text);
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine(text);
            }

            _length = 0;
        }
    }
}
