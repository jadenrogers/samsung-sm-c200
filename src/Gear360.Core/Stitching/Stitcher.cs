using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace Gear360.Core.Stitching;

/// <summary>Stitching failed (ffmpeg error, unreadable input, ...).</summary>
public sealed class StitchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public StitchException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The outcome of stitching one file.</summary>
/// <param name="InputPath">The dual-fisheye input.</param>
/// <param name="OutputPath">The equirectangular output.</param>
/// <param name="Skipped">True when the output already existed and was left alone.</param>
/// <param name="Encoder">The encoder that wrote the video; null for photos and skipped files.</param>
/// <param name="FallbackReason">
/// Set when the hardware encoder Auto picked failed on this file and it was stitched again on the CPU.
/// </param>
/// <param name="TuningSkipped">
/// True when high-quality tuning was asked for but this graphics card does not support it, so the encoder's standard
/// settings were used.
/// </param>
public sealed record StitchResult(string InputPath, string OutputPath, bool Skipped, StitchEncoder? Encoder = null, string? FallbackReason = null, bool TuningSkipped = false);

/// <summary>Width, height and duration of a media file, as reported by ffprobe.</summary>
internal sealed record MediaInfo(int Width, int Height, TimeSpan? Duration);

/// <summary>
/// Turns the side-by-side dual-fisheye frames the Gear 360 records into equirectangular (2:1) 360 media using
/// ffmpeg's <c>v360</c> filter, then marks videos as spherical with <see cref="SphericalMetadataInjector"/>.
/// </summary>
public sealed class Stitcher
{
    /// <summary>Suffix added to the input's name to form the output's name.</summary>
    public const string OutputSuffix = "_stitched";

    /// <summary>Suffix of the temporary file written while ffmpeg runs.</summary>
    public const string PartialSuffix = ".part";

    private readonly FfmpegTools _tools;
    private readonly EncoderDetector _detector;
    private readonly ToolRunner _run;

    /// <summary>Creates a stitcher that uses the given ffmpeg and ffprobe.</summary>
    public Stitcher(FfmpegTools tools)
        : this(tools, EncoderDetector.Shared, ToolProcess.RunAsync)
    {
    }

    /// <summary>Creates a stitcher with a replaceable encoder detector and process runner (for tests).</summary>
    internal Stitcher(FfmpegTools tools, EncoderDetector detector, ToolRunner run)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(run);
        _tools = tools;
        _detector = detector;
        _run = run;
    }

    /// <summary>
    /// The encoder videos will be written with: <see cref="StitchOptions.Encoder"/>, with Auto resolved to the first
    /// usable hardware encoder or the CPU. The answer is cached, so asking first costs nothing later.
    /// </summary>
    /// <exception cref="StitchException">An explicitly chosen hardware encoder is not usable on this computer.</exception>
    public Task<StitchEncoder> ResolveEncoderAsync(StitchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _detector.ResolveAsync(_tools.FfmpegPath, options.Encoder, options.Codec, cancellationToken);
    }

    /// <summary>True for a file this stitcher wrote (its name ends in <see cref="OutputSuffix"/>).</summary>
    public static bool IsStitchedOutput(string path) =>
        Path.GetFileNameWithoutExtension(path).EndsWith(OutputSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The output path for <paramref name="inputPath"/>: <c>&lt;name&gt;_stitched.mp4</c> (or <c>.jpg</c> for photos).</summary>
    public static string GetOutputPath(string inputPath, StitchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var full = Path.GetFullPath(inputPath);
        var folder = options.OutputDirectory is null ? Path.GetDirectoryName(full)! : Path.GetFullPath(options.OutputDirectory);
        var extension = MediaKinds.FromPath(full) == MediaKind.Photo ? ".jpg" : ".mp4";
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(full) + OutputSuffix + extension);
    }

    /// <summary>Stitches one video or photo.</summary>
    /// <param name="inputPath">A dual-fisheye .mp4/.mov video or .jpg photo.</param>
    /// <param name="options">Geometry, encoding and output settings.</param>
    /// <param name="progress">Receives the fraction done, 0 to 1.</param>
    /// <param name="cancellationToken">Stops ffmpeg and removes the partial output.</param>
    /// <exception cref="StitchException">ffmpeg failed or the input could not be read.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the partial output has been removed.</exception>
    public async Task<StitchResult> StitchAsync(
        string inputPath,
        StitchOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var input = Path.GetFullPath(inputPath);
        if (!File.Exists(input))
        {
            throw new StitchException($"File not found: {input}");
        }

        var kind = MediaKinds.FromPath(input);
        if (kind == MediaKind.Other)
        {
            throw new StitchException($"Not a video or photo: {input}");
        }

        var output = GetOutputPath(input, options);
        if (string.Equals(output, input, StringComparison.OrdinalIgnoreCase))
        {
            throw new StitchException($"The output would overwrite the input: {input}");
        }

        if (File.Exists(output) && !options.Overwrite)
        {
            return new StitchResult(input, output, Skipped: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var info = await ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        var partial = output + PartialSuffix;
        StitchEncoder? encoder = kind == MediaKind.Video
            ? await ResolveEncoderAsync(options, cancellationToken).ConfigureAwait(false)
            : null;
        string? fallbackReason = null;
        var tuningSkipped = false;
        if (encoder is { } hardware && options.HighQualityEncoderTuning && StitchEncoders.HasHighQualityTuning(hardware) &&
            !await _detector.SupportsHighQualityTuningAsync(_tools.FfmpegPath, hardware, options.Codec, cancellationToken).ConfigureAwait(false))
        {
            // Older cards may reject some of the tuning flags (B-frame references in particular); use the
            // encoder's standard settings rather than failing or leaving the graphics card.
            options = options with { HighQualityEncoderTuning = false };
            tuningSkipped = true;
        }

        try
        {
            var arguments = BuildArguments(input, partial, info, options, kind, encoder ?? StitchEncoder.Cpu);
            var result = await RunAsync(_tools.FfmpegPath, arguments, ProgressParser(info.Duration, progress), cancellationToken).ConfigureAwait(false);
            if ((result.ExitCode != 0 || !File.Exists(partial)) &&
                options.Encoder == StitchEncoder.Auto && encoder is { } failed && failed.IsHardware())
            {
                // A hardware encoder can pass the test encode and still fail on a real file (a size the chip does
                // not support, a busy GPU, a driver hiccup): in Auto mode, try once more on the CPU.
                TryDelete(partial);
                fallbackReason = $"{failed.DisplayName()} failed (exit code {result.ExitCode}: {LastLine(result.ErrorTail)})";
                encoder = StitchEncoder.Cpu;
                progress?.Report(0);
                arguments = BuildArguments(input, partial, info, options, kind, StitchEncoder.Cpu);
                result = await RunAsync(_tools.FfmpegPath, arguments, ProgressParser(info.Duration, progress), cancellationToken).ConfigureAwait(false);
            }

            if (result.ExitCode != 0 || !File.Exists(partial))
            {
                throw new StitchException(
                    $"ffmpeg failed to stitch {Path.GetFileName(input)} (exit code {result.ExitCode}).{Environment.NewLine}{result.ErrorTail}");
            }

            if (kind == MediaKind.Video && options.InjectMetadata)
            {
                try
                {
                    SphericalMetadataInjector.Inject(partial);
                }
                catch (InvalidDataException ex)
                {
                    throw new StitchException($"Could not add 360 metadata to {Path.GetFileName(output)}: {ex.Message}", ex);
                }
            }

            File.Move(partial, output, overwrite: options.Overwrite);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        progress?.Report(1);
        return new StitchResult(input, output, Skipped: false, encoder, fallbackReason, tuningSkipped);
    }

    /// <summary>Reads the size and duration of the first video stream.</summary>
    internal async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        string[] arguments =
        [
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height:format=duration",
            "-of", "json",
            path,
        ];
        var result = await RunAsync(_tools.FfprobePath, arguments, null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new StitchException($"ffprobe could not read {Path.GetFileName(path)}.{Environment.NewLine}{result.ErrorTail}");
        }

        try
        {
            return ParseProbeOutput(result.StandardOutput);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new StitchException($"ffprobe found no video stream in {Path.GetFileName(path)}.", ex);
        }
    }

    /// <summary>Parses ffprobe's <c>-of json</c> output.</summary>
    internal static MediaInfo ParseProbeOutput(string json)
    {
        using var document = JsonDocument.Parse(json);
        var stream = document.RootElement.GetProperty("streams")[0];
        var width = stream.GetProperty("width").GetInt32();
        var height = stream.GetProperty("height").GetInt32();
        TimeSpan? duration = null;
        if (document.RootElement.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durationText) &&
            double.TryParse(durationText.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0)
        {
            duration = TimeSpan.FromSeconds(seconds);
        }

        return new MediaInfo(width, height, duration);
    }

    /// <summary>Builds the <c>v360</c> filter that maps side-by-side dual fisheye to equirectangular.</summary>
    /// <remarks>
    /// ffmpeg's <c>dfisheye</c> input reads the left half of the frame as the front lens and the right half as the
    /// back lens, which is how the Gear 360 lays out its frames. The output keeps the input's width at 2:1.
    /// </remarks>
    internal static string BuildFilter(MediaInfo info, StitchOptions options)
    {
        // Even dimensions keep 4:2:0 encoders happy: width a multiple of 4 makes the height (width / 2) even too.
        var width = Math.Max(4, info.Width - (info.Width % 4));
        var height = width / 2;
        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"v360=input=dfisheye:output=e:ih_fov={options.Fov:0.###}:iv_fov={options.Fov:0.###}:w={width}:h={height}");
        if (options.Yaw != 0)
        {
            filter += string.Create(CultureInfo.InvariantCulture, $":yaw={options.Yaw:0.###}");
        }

        if (options.Pitch != 0)
        {
            filter += string.Create(CultureInfo.InvariantCulture, $":pitch={options.Pitch:0.###}");
        }

        if (options.Roll != 0)
        {
            filter += string.Create(CultureInfo.InvariantCulture, $":roll={options.Roll:0.###}");
        }

        if (options.Interpolation != StitchInterpolation.Linear)
        {
            // Bilinear is v360's default, so it is left out.
            filter += ":interp=" + options.Interpolation.FfmpegName();
        }

        return filter;
    }

    /// <summary>Builds the ffmpeg command line (without the executable).</summary>
    /// <param name="input">The input file.</param>
    /// <param name="output">The file ffmpeg writes.</param>
    /// <param name="info">The input's size.</param>
    /// <param name="options">Geometry and encoding settings.</param>
    /// <param name="kind">Video or photo.</param>
    /// <param name="encoder">The resolved video encoder (never Auto); ignored for photos.</param>
    internal static IReadOnlyList<string> BuildArguments(string input, string output, MediaInfo info, StitchOptions options, MediaKind kind, StitchEncoder encoder = StitchEncoder.Cpu)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-v", "error",
            "-i", input,
            "-vf", BuildFilter(info, options),
        };

        if (kind == MediaKind.Photo)
        {
            arguments.AddRange(["-frames:v", "1", "-c:v", "mjpeg", "-q:v", "2", "-update", "1", "-f", "image2", output]);
            return arguments;
        }

        arguments.AddRange(["-map", "0:v:0", "-map", "0:a?", "-map_metadata", "0"]);
        arguments.AddRange(StitchEncoders.BuildArguments(encoder, options));
        arguments.AddRange(options.Audio == StitchAudio.Aac ? ["-c:a", "aac", "-b:a", "192k"] : ["-c:a", "copy"]);
        arguments.AddRange(
        [
            "-movflags", "+faststart",
            "-progress", "pipe:1",
            "-nostats",
            "-f", "mp4",
            output,
        ]);
        return arguments;
    }

    /// <summary>Turns ffmpeg <c>-progress</c> lines into fractions of <paramref name="duration"/>.</summary>
    internal static Action<string>? ProgressParser(TimeSpan? duration, IProgress<double>? progress)
    {
        if (progress is null || duration is not { TotalMicroseconds: > 0 } total)
        {
            return null;
        }

        return line =>
        {
            // out_time_ms is in microseconds too (a long-standing ffmpeg quirk); prefer out_time_us when present.
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                return;
            }

            var key = line.AsSpan(0, separator);
            if (!key.SequenceEqual("out_time_us") && !key.SequenceEqual("out_time_ms"))
            {
                return;
            }

            if (long.TryParse(line.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) && microseconds >= 0)
            {
                progress.Report(Math.Clamp(microseconds / total.TotalMicroseconds, 0, 1));
            }
        };
    }

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "no error message" : lines[^1];
    }

    private async Task<ToolResult> RunAsync(string tool, IReadOnlyList<string> arguments, Action<string>? onOutputLine, CancellationToken cancellationToken)
    {
        try
        {
            return await _run(tool, arguments, onOutputLine, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            throw new StitchException($"Could not start {tool}: {ex.Message}", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
