using System.Globalization;

namespace Gear360.Core.Stitching;

/// <summary>A ready-made set of quality settings for stitching.</summary>
public enum StitchProfile
{
    /// <summary>Quick previews: H.264 CRF 23, a fast preset, bilinear sampling.</summary>
    Fast,

    /// <summary>The default: H.264 CRF 20, medium preset, bilinear sampling.</summary>
    Balanced,

    /// <summary>For uploading (YouTube): H.264 CRF 16, slow preset, the hardware encoder's best settings.</summary>
    High,

    /// <summary>For keeping: HEVC CRF 14, slow preset, best hardware settings and Lanczos sampling.</summary>
    Max,

    /// <summary>Every setting chosen by hand.</summary>
    Custom,
}

/// <summary>
/// Settings given explicitly on top of a <see cref="StitchProfile"/> (command-line flags, for instance). Each non-null
/// value replaces the profile's.
/// </summary>
public sealed record StitchOverrides
{
    /// <inheritdoc cref="StitchOptions.Fov"/>
    public double? Fov { get; init; }

    /// <inheritdoc cref="StitchOptions.Yaw"/>
    public double? Yaw { get; init; }

    /// <inheritdoc cref="StitchOptions.Pitch"/>
    public double? Pitch { get; init; }

    /// <inheritdoc cref="StitchOptions.Roll"/>
    public double? Roll { get; init; }

    /// <inheritdoc cref="StitchOptions.Codec"/>
    public StitchCodec? Codec { get; init; }

    /// <inheritdoc cref="StitchOptions.Encoder"/>
    public StitchEncoder? Encoder { get; init; }

    /// <inheritdoc cref="StitchOptions.Crf"/>
    public int? Crf { get; init; }

    /// <inheritdoc cref="StitchOptions.Preset"/>
    public string? Preset { get; init; }

    /// <inheritdoc cref="StitchOptions.Interpolation"/>
    public StitchInterpolation? Interpolation { get; init; }

    /// <inheritdoc cref="StitchOptions.HighQualityEncoderTuning"/>
    public bool? HighQualityEncoderTuning { get; init; }

    /// <inheritdoc cref="StitchOptions.Audio"/>
    public StitchAudio? Audio { get; init; }
}

/// <summary>
/// The quality profiles as data. A profile sets the codec, CRF, preset, interpolation and hardware tuning; the lens
/// geometry (FOV, yaw, pitch, roll), the encoder and the audio handling are independent of it.
/// </summary>
public static class StitchProfiles
{
    /// <summary>The profiles that stand for a fixed set of settings (all but <see cref="StitchProfile.Custom"/>).</summary>
    public static IReadOnlyList<StitchProfile> Named { get; } = [StitchProfile.Fast, StitchProfile.Balanced, StitchProfile.High, StitchProfile.Max];

    /// <summary>The settings <paramref name="profile"/> stands for; Custom gives Balanced's, as a starting point.</summary>
    public static StitchOptions Get(StitchProfile profile) => ApplyTo(new StitchOptions(), profile);

    /// <summary>
    /// <paramref name="options"/> with the quality settings of <paramref name="profile"/>; every other setting is kept.
    /// Custom returns <paramref name="options"/> unchanged.
    /// </summary>
    public static StitchOptions ApplyTo(StitchOptions options, StitchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(options);
        return profile switch
        {
            StitchProfile.Fast => options with
            {
                Codec = StitchCodec.H264, Crf = 23, Preset = "veryfast",
                Interpolation = StitchInterpolation.Linear, HighQualityEncoderTuning = false,
            },
            StitchProfile.Balanced => options with
            {
                Codec = StitchCodec.H264, Crf = 20, Preset = "medium",
                Interpolation = StitchInterpolation.Linear, HighQualityEncoderTuning = false,
            },
            StitchProfile.High => options with
            {
                Codec = StitchCodec.H264, Crf = 16, Preset = "slow",
                Interpolation = StitchInterpolation.Linear, HighQualityEncoderTuning = true,
            },
            StitchProfile.Max => options with
            {
                Codec = StitchCodec.Hevc, Crf = 14, Preset = "slow",
                Interpolation = StitchInterpolation.Lanczos, HighQualityEncoderTuning = true,
            },
            StitchProfile.Custom => options,
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown profile."),
        };
    }

    /// <summary>
    /// The profile first, then every explicit override on top: what <c>--quality high --crf 18</c> means.
    /// </summary>
    public static StitchOptions Apply(StitchProfile profile, StitchOverrides? overrides = null, StitchOptions? baseOptions = null)
    {
        var options = ApplyTo(baseOptions ?? new StitchOptions(), profile);
        if (overrides is null)
        {
            return options;
        }

        return options with
        {
            Fov = overrides.Fov ?? options.Fov,
            Yaw = overrides.Yaw ?? options.Yaw,
            Pitch = overrides.Pitch ?? options.Pitch,
            Roll = overrides.Roll ?? options.Roll,
            Codec = overrides.Codec ?? options.Codec,
            Encoder = overrides.Encoder ?? options.Encoder,
            Crf = overrides.Crf ?? options.Crf,
            Preset = overrides.Preset ?? options.Preset,
            Interpolation = overrides.Interpolation ?? options.Interpolation,
            HighQualityEncoderTuning = overrides.HighQualityEncoderTuning ?? options.HighQualityEncoderTuning,
            Audio = overrides.Audio ?? options.Audio,
        };
    }

    /// <summary>The named profile whose quality settings <paramref name="options"/> has, or Custom when none.</summary>
    public static StitchProfile Match(StitchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var profile in Named)
        {
            if (ApplyTo(options, profile) == options)
            {
                return profile;
            }
        }

        return StitchProfile.Custom;
    }

    /// <summary>A one-line description for pickers and help: what the profile is for, and its cost.</summary>
    public static string Describe(StitchProfile profile) => profile switch
    {
        StitchProfile.Fast => "Quick previews: the smallest files; on the CPU twice as fast as Balanced.",
        StitchProfile.Balanced => "Good quality at about 14 Mbit/s (the default).",
        StitchProfile.High => "For YouTube uploads: H.264 at about 32-40 Mbit/s; files 2-3x Balanced's, about 1.5x the time.",
        StitchProfile.Max => "For keeping: HEVC at about 38 Mbit/s with sharper sampling; slowest (1.7x High on a graphics card, 4.5x on the CPU).",
        StitchProfile.Custom => "Your own settings (see Advanced).",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown profile."),
    };

    /// <summary>The <c>--quality</c> name of a profile, e.g. <c>high</c>.</summary>
    public static string CliName(this StitchProfile profile) => profile.ToString().ToLowerInvariant();

    /// <summary>
    /// A one-line summary of what will actually be used, e.g.
    /// <c>h264_nvenc, CQ 21, preset p7, HQ tuning, interp line, audio copy, FOV 195</c>.
    /// </summary>
    /// <param name="options">The effective options.</param>
    /// <param name="encoder">The resolved encoder (not Auto).</param>
    public static string Summarize(StitchOptions options, StitchEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(options);
        var parts = new List<string> { encoder.FfmpegName(options.Codec) };
        parts.AddRange(StitchEncoders.DescribeQuality(encoder, options));
        if (options.HighQualityEncoderTuning && StitchEncoders.HasHighQualityTuning(encoder))
        {
            parts.Add("HQ tuning");
        }

        parts.Add("interp " + options.Interpolation.FfmpegName());
        parts.Add(options.Audio == StitchAudio.Copy ? "audio copy" : "audio AAC 192k");
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"FOV {options.Fov:0.###}"));
        if (options.Yaw != 0 || options.Pitch != 0 || options.Roll != 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"yaw/pitch/roll {options.Yaw:0.###}/{options.Pitch:0.###}/{options.Roll:0.###}"));
        }

        return string.Join(", ", parts);
    }

    /// <summary>The v360 <c>interp</c> value, e.g. <c>lanczos</c>.</summary>
    public static string FfmpegName(this StitchInterpolation interpolation) => interpolation switch
    {
        StitchInterpolation.Linear => "line",
        StitchInterpolation.Cubic => "cubic",
        StitchInterpolation.Lanczos => "lanczos",
        StitchInterpolation.Spline16 => "spline16",
        StitchInterpolation.Gaussian => "gauss",
        StitchInterpolation.Mitchell => "mitchell",
        StitchInterpolation.Lagrange9 => "lagrange9",
        StitchInterpolation.Nearest => "near",
        _ => throw new ArgumentOutOfRangeException(nameof(interpolation), interpolation, null),
    };
}
