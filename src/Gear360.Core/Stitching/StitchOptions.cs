namespace Gear360.Core.Stitching;

/// <summary>The video codec of stitched output.</summary>
public enum StitchCodec
{
    /// <summary>H.264 (libx264): plays everywhere.</summary>
    H264,

    /// <summary>H.265/HEVC (libx265): smaller files, slower to encode.</summary>
    Hevc,
}

/// <summary>How v360 samples the fisheye pictures when it builds each output pixel (its <c>interp</c> option).</summary>
public enum StitchInterpolation
{
    /// <summary>Bilinear (<c>line</c>), ffmpeg's default: the fastest, and nearly as sharp as the others.</summary>
    Linear,

    /// <summary>Bicubic (<c>cubic</c>).</summary>
    Cubic,

    /// <summary>Lanczos (<c>lanczos</c>): a little sharper, about twice as slow as bilinear.</summary>
    Lanczos,

    /// <summary>Spline16 (<c>spline16</c>).</summary>
    Spline16,

    /// <summary>Gaussian (<c>gauss</c>): softer.</summary>
    Gaussian,

    /// <summary>Mitchell (<c>mitchell</c>).</summary>
    Mitchell,

    /// <summary>Lagrange9 (<c>lagrange9</c>).</summary>
    Lagrange9,

    /// <summary>Nearest neighbour (<c>near</c>): blocky; for previews only.</summary>
    Nearest,
}

/// <summary>What happens to the video's sound.</summary>
public enum StitchAudio
{
    /// <summary>Copied unchanged (the camera records AAC): no quality loss, no time.</summary>
    Copy,

    /// <summary>Re-encoded to AAC at 192 kbit/s, for players that cannot handle the original sound track.</summary>
    Aac,
}

/// <summary>Settings for <see cref="Stitcher"/>.</summary>
public sealed record StitchOptions
{
    /// <summary>The x264/x265 preset names, fastest first.</summary>
    public static IReadOnlyList<string> Presets { get; } =
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];

    /// <summary>The field of view of each fisheye lens, in degrees. The SM-C200 lenses cover about 195°.</summary>
    public double Fov { get; init; } = 195;

    /// <summary>Rotation of the output around the vertical axis, in degrees (-180 to 180).</summary>
    public double Yaw { get; init; }

    /// <summary>Tilt of the output, in degrees (-180 to 180).</summary>
    public double Pitch { get; init; }

    /// <summary>Roll of the output, in degrees (-180 to 180).</summary>
    public double Roll { get; init; }

    /// <summary>The video codec.</summary>
    public StitchCodec Codec { get; init; } = StitchCodec.H264;

    /// <summary>
    /// Which encoder writes the video. Auto (the default) uses a working hardware encoder when there is one and the
    /// CPU otherwise; see <see cref="StitchEncoder"/>.
    /// </summary>
    public StitchEncoder Encoder { get; init; } = StitchEncoder.Auto;

    /// <summary>
    /// The x264/x265 constant rate factor: lower is better quality and larger (0 to 51). Hardware encoders get an
    /// equivalent quality setting that gives files of about the same size.
    /// </summary>
    public int Crf { get; init; } = 20;

    /// <summary>The x264/x265 preset, from <c>ultrafast</c> to <c>veryslow</c>.</summary>
    public string Preset { get; init; } = "medium";

    /// <summary>How v360 samples the fisheye pictures; see <see cref="StitchInterpolation"/>.</summary>
    public StitchInterpolation Interpolation { get; init; } = StitchInterpolation.Linear;

    /// <summary>
    /// Use a hardware encoder's slowest preset and its quality features (for NVENC: p7, two-pass, adaptive
    /// quantisation, lookahead and B-frame references). Ignored by the CPU encoders, whose <see cref="Preset"/> already
    /// sets this, and dropped when the graphics card does not support it.
    /// </summary>
    public bool HighQualityEncoderTuning { get; init; }

    /// <summary>What happens to the sound track.</summary>
    public StitchAudio Audio { get; init; } = StitchAudio.Copy;

    /// <summary>Folder for the output; null puts it next to the input.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Replace an existing output file instead of skipping the input.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Write the spherical (360) metadata into stitched videos so players recognise them.</summary>
    public bool InjectMetadata { get; init; } = true;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when a value is out of range.</summary>
    public void Validate()
    {
        if (Fov is <= 0 or > 360 || double.IsNaN(Fov))
        {
            throw new ArgumentOutOfRangeException(nameof(Fov), Fov, "The field of view must be between 0 and 360 degrees.");
        }

        CheckAngle(Yaw, nameof(Yaw));
        CheckAngle(Pitch, nameof(Pitch));
        CheckAngle(Roll, nameof(Roll));
        if (Crf is < 0 or > 51)
        {
            throw new ArgumentOutOfRangeException(nameof(Crf), Crf, "CRF must be between 0 and 51.");
        }

        if (!Enum.IsDefined(Encoder))
        {
            throw new ArgumentOutOfRangeException(nameof(Encoder), Encoder, "Unknown encoder.");
        }

        if (!Enum.IsDefined(Interpolation))
        {
            throw new ArgumentOutOfRangeException(nameof(Interpolation), Interpolation, "Unknown interpolation.");
        }

        if (!Enum.IsDefined(Audio))
        {
            throw new ArgumentOutOfRangeException(nameof(Audio), Audio, "Unknown audio setting.");
        }

        if (string.IsNullOrWhiteSpace(Preset) || !Preset.All(char.IsAsciiLetterLower))
        {
            throw new ArgumentOutOfRangeException(nameof(Preset), Preset, "The preset must be a name such as 'medium' or 'slow'.");
        }
    }

    private static void CheckAngle(double value, string name)
    {
        if (value is < -180 or > 180 || double.IsNaN(value))
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between -180 and 180 degrees.");
        }
    }
}
