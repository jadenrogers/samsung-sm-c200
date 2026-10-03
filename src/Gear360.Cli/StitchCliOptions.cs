using System.CommandLine;
using System.CommandLine.Parsing;
using Gear360.Core.Stitching;

namespace Gear360.Cli;

/// <summary>The stitching options shared by <c>gear360 stitch</c> and <c>gear360 copy --stitch</c>.</summary>
internal sealed class StitchCliOptions
{
    public Option<double> Fov { get; } = new("--fov")
    {
        Description = "Field of view of each fisheye lens, in degrees.",
        DefaultValueFactory = _ => 195,
        HelpName = "degrees",
    };

    public Option<double> Yaw { get; } = new("--yaw") { Description = "Rotate the 360 view left/right, in degrees (-180 to 180).", HelpName = "degrees" };

    public Option<double> Pitch { get; } = new("--pitch") { Description = "Tilt the 360 view up/down, in degrees (-180 to 180).", HelpName = "degrees" };

    public Option<double> Roll { get; } = new("--roll") { Description = "Roll the 360 view, in degrees (-180 to 180).", HelpName = "degrees" };

    public Option<string> Quality { get; } = new Option<string>("--quality", "-q")
    {
        Description = "Quality profile: fast (previews), balanced, high (YouTube uploads) or max (archive). " +
            "--codec, --crf, --preset, --interp and --hq-tuning given as well replace the profile's value.",
        DefaultValueFactory = _ => "balanced",
        HelpName = "profile",
    }.AcceptOnlyFromAmong("fast", "balanced", "high", "max");

    public Option<string?> Codec { get; } = new Option<string?>("--codec")
    {
        Description = "Video codec of the stitched file: h264 (plays everywhere) or hevc (smaller). Default: from --quality.",
    }.AcceptOnlyFromAmong("h264", "hevc");

    public Option<string> Encoder { get; } = new Option<string>("--encoder")
    {
        Description = "Who encodes the video: auto (a working graphics card encoder, else the CPU), cpu, nvidia, intel, amd or apple.",
        DefaultValueFactory = _ => "auto",
    }.AcceptOnlyFromAmong("auto", "cpu", "nvidia", "intel", "amd", "apple");

    public Option<int?> Crf { get; } = new("--crf")
    {
        Description = "Quality, 0-51: lower is better and larger. Default: from --quality.",
        HelpName = "0-51",
    };

    public Option<string?> Preset { get; } = new Option<string?>("--preset")
    {
        Description = "Encoder speed, ultrafast to veryslow: slower presets give smaller files. Mapped to the nearest preset of a hardware encoder. Default: from --quality.",
        HelpName = "name",
    }.AcceptOnlyFromAmong([.. StitchOptions.Presets]);

    public Option<string?> Interpolation { get; } = new Option<string?>("--interp")
    {
        Description = "How the fisheye pictures are sampled: line (fast), cubic, lanczos (sharpest, about 2x slower), spline16, gauss, mitchell, lagrange9 or near. Default: from --quality.",
        HelpName = "method",
    }.AcceptOnlyFromAmong(InterpolationNames);

    public Option<string?> HqTuning { get; } = new Option<string?>("--hq-tuning")
    {
        Description = "on: the graphics card encoder's slowest preset and quality features (NVENC p7, two-pass, adaptive quantisation, lookahead). Default: from --quality (on for high and max).",
        HelpName = "on|off",
    }.AcceptOnlyFromAmong("on", "off");

    public Option<string> Audio { get; } = new Option<string>("--audio")
    {
        Description = "Sound: copy (unchanged) or aac (re-encode to AAC 192k, for players that cannot play the original).",
        DefaultValueFactory = _ => "copy",
    }.AcceptOnlyFromAmong("copy", "aac");

    public Option<string?> Ffmpeg { get; } = new("--ffmpeg")
    {
        Description = "Path to ffmpeg (or its folder) when it is not on PATH.",
        HelpName = "path",
    };

    public Option<bool> GetFfmpeg { get; } = new("--get-ffmpeg")
    {
        Description = "If ffmpeg is not found, download it first (same as running 'gear360 ffmpeg install').",
    };

    public StitchCliOptions()
    {
        Fov.Validators.Add(r => Check(r, r.GetValueOrDefault<double>() is > 0 and <= 360, "--fov must be between 0 and 360."));
        Crf.Validators.Add(r => Check(r, r.GetValueOrDefault<int?>() is null or (>= 0 and <= 51), "--crf must be between 0 and 51."));
        foreach (var angle in new[] { Yaw, Pitch, Roll })
        {
            angle.Validators.Add(r => Check(r, r.GetValueOrDefault<double>() is >= -180 and <= 180, $"{r.Option.Name} must be between -180 and 180."));
        }
    }

    /// <summary>All options, to add to a command.</summary>
    public IEnumerable<Option> All => [Quality, Fov, Yaw, Pitch, Roll, Codec, Encoder, Crf, Preset, Interpolation, HqTuning, Audio, Ffmpeg, GetFfmpeg];

    /// <summary>The <c>--interp</c> names, in <see cref="StitchInterpolation"/> order.</summary>
    internal static string[] InterpolationNames => [.. Enum.GetValues<StitchInterpolation>().Select(i => i.FfmpegName())];

    /// <summary>Parses an <c>--encoder</c> value (already restricted to the allowed names).</summary>
    internal static StitchEncoder ParseEncoder(string? value) =>
        Enum.TryParse<StitchEncoder>(value, ignoreCase: true, out var encoder) ? encoder : StitchEncoder.Auto;

    /// <summary>Parses a <c>--quality</c> value (already restricted to the allowed names).</summary>
    internal static StitchProfile ParseProfile(string? value) =>
        Enum.TryParse<StitchProfile>(value, ignoreCase: true, out var profile) && profile != StitchProfile.Custom ? profile : StitchProfile.Balanced;

    /// <summary>
    /// Builds <see cref="StitchOptions"/> from a parse result: the <c>--quality</c> profile first, then every option
    /// that was given explicitly on top of it.
    /// </summary>
    public (StitchProfile Profile, StitchOptions Options) Read(ParseResult parseResult, string? outputDirectory, bool overwrite)
    {
        var profile = ParseProfile(parseResult.GetValue(Quality));
        var overrides = new StitchOverrides
        {
            Fov = parseResult.GetValue(Fov),
            Yaw = parseResult.GetValue(Yaw),
            Pitch = parseResult.GetValue(Pitch),
            Roll = parseResult.GetValue(Roll),
            Codec = parseResult.GetValue(Codec) switch { "hevc" => StitchCodec.Hevc, "h264" => StitchCodec.H264, _ => null },
            Encoder = ParseEncoder(parseResult.GetValue(Encoder)),
            Crf = parseResult.GetValue(Crf),
            Preset = parseResult.GetValue(Preset),
            Interpolation = parseResult.GetValue(Interpolation) is { } interp
                ? Enum.GetValues<StitchInterpolation>().First(i => i.FfmpegName() == interp)
                : null,
            HighQualityEncoderTuning = parseResult.GetValue(HqTuning) switch { "on" => true, "off" => false, _ => null },
            Audio = parseResult.GetValue(Audio) == "aac" ? StitchAudio.Aac : StitchAudio.Copy,
        };
        return (profile, StitchProfiles.Apply(profile, overrides, new StitchOptions { OutputDirectory = outputDirectory, Overwrite = overwrite }));
    }

    private static void Check(OptionResult result, bool valid, string message)
    {
        if (!valid)
        {
            result.AddError(message);
        }
    }
}
