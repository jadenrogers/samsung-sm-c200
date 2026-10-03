using System.Globalization;

namespace Gear360.Core.Stitching;

/// <summary>Which encoder turns the stitched frames into H.264/HEVC.</summary>
/// <remarks>
/// Only the final encode runs on the graphics card: decoding and the <c>v360</c> dewarp filter stay on the CPU, so a
/// hardware encoder speeds stitching up by a factor of two to three rather than by an order of magnitude.
/// </remarks>
public enum StitchEncoder
{
    /// <summary>The first working hardware encoder on this machine, else the CPU.</summary>
    Auto,

    /// <summary>The CPU (libx264/libx265): the slowest, and the smallest files for a given quality.</summary>
    Cpu,

    /// <summary>NVIDIA graphics cards (NVENC).</summary>
    Nvidia,

    /// <summary>Intel graphics (Quick Sync Video).</summary>
    Intel,

    /// <summary>AMD graphics cards (AMF).</summary>
    Amd,

    /// <summary>Macs (VideoToolbox).</summary>
    Apple,
}

/// <summary>ffmpeg encoder names and command-line arguments for each <see cref="StitchEncoder"/>.</summary>
public static class StitchEncoders
{
    /// <summary>
    /// Added to the CRF to get the hardware encoders' quantizer. Hardware encoders spend more bits than x264/x265 at
    /// the same number, so the CRF is shifted to keep files of about the same size and quality.
    /// </summary>
    /// <remarks>
    /// Calibrated with NVENC (RTX 3080, ffmpeg 6.0) on a 10 s 3840x1920 Gear 360 clip: libx264 CRF 20 gave 18.6 MB at
    /// 46.5 dB PSNR, h264_nvenc CQ 25 16.3 MB at 47.2 dB; libx265 CRF 20 gave 9.1 MB at 46.7 dB, hevc_nvenc CQ 27
    /// 9.1 MB at 46.5 dB. Quick Sync and AMF use the same offsets, untested.
    /// </remarks>
    internal const int H264QualityOffset = 5;

    /// <inheritdoc cref="H264QualityOffset"/>
    internal const int HevcQualityOffset = 7;

    /// <summary>The <c>-maxrate</c> given to hevc_nvenc so that its hidden default cap does not override the CQ.</summary>
    internal const string HevcNvencMaxRate = "400M";

    /// <summary>The hardware encoders, in no particular order.</summary>
    public static IReadOnlyList<StitchEncoder> Hardware { get; } = [StitchEncoder.Nvidia, StitchEncoder.Intel, StitchEncoder.Amd, StitchEncoder.Apple];

    /// <summary>True for the encoders that run on a graphics card or media engine.</summary>
    public static bool IsHardware(this StitchEncoder encoder) => encoder is not (StitchEncoder.Auto or StitchEncoder.Cpu);

    /// <summary>A short name for messages, e.g. "NVIDIA NVENC".</summary>
    public static string DisplayName(this StitchEncoder encoder) => encoder switch
    {
        StitchEncoder.Auto => "Auto",
        StitchEncoder.Cpu => "CPU",
        StitchEncoder.Nvidia => "NVIDIA NVENC",
        StitchEncoder.Intel => "Intel Quick Sync",
        StitchEncoder.Amd => "AMD AMF",
        StitchEncoder.Apple => "Apple VideoToolbox",
        _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, null),
    };

    /// <summary>The ffmpeg encoder name, e.g. <c>h264_nvenc</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="encoder"/> is <see cref="StitchEncoder.Auto"/>, which must be resolved first.</exception>
    public static string FfmpegName(this StitchEncoder encoder, StitchCodec codec)
    {
        var hevc = codec == StitchCodec.Hevc;
        return encoder switch
        {
            StitchEncoder.Cpu => hevc ? "libx265" : "libx264",
            StitchEncoder.Nvidia => hevc ? "hevc_nvenc" : "h264_nvenc",
            StitchEncoder.Intel => hevc ? "hevc_qsv" : "h264_qsv",
            StitchEncoder.Amd => hevc ? "hevc_amf" : "h264_amf",
            StitchEncoder.Apple => hevc ? "hevc_videotoolbox" : "h264_videotoolbox",
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Resolve Auto to a concrete encoder first."),
        };
    }

    /// <summary>The quantizer a hardware encoder gets for <paramref name="crf"/> (see <see cref="H264QualityOffset"/>).</summary>
    internal static int HardwareQuality(StitchCodec codec, int crf) =>
        Math.Clamp(crf + (codec == StitchCodec.Hevc ? HevcQualityOffset : H264QualityOffset), 1, 51);

    /// <summary>VideoToolbox's <c>-q:v</c> (1 to 100, higher is better) for <paramref name="crf"/>.</summary>
    internal static int VideoToolboxQuality(int crf) => Math.Clamp(100 - (2 * crf), 1, 100);

    /// <summary>
    /// True for the encoders <see cref="StitchOptions.HighQualityEncoderTuning"/> changes: NVENC, Quick Sync and AMF.
    /// The CPU encoders take their quality from the preset alone, and VideoToolbox has no comparable settings.
    /// </summary>
    public static bool HasHighQualityTuning(StitchEncoder encoder) => encoder is StitchEncoder.Nvidia or StitchEncoder.Intel or StitchEncoder.Amd;

    /// <summary>
    /// The arguments high-quality tuning adds for NVENC: two passes at full resolution, spatial and temporal adaptive
    /// quantisation, a 32-frame rate-control lookahead and three B-frames used as references. Older cards may reject some
    /// of these (B-frame references in particular), so <see cref="EncoderDetector"/> checks before they are used.
    /// </summary>
    internal static IReadOnlyList<string> NvencTuningArguments { get; } =
    [
        "-multipass", "fullres", "-spatial_aq", "1", "-temporal_aq", "1",
        "-rc-lookahead", "32", "-bf", "3", "-b_ref_mode", "middle",
    ];

    /// <summary>
    /// The video encoding arguments, from <c>-c:v</c> to <c>-pix_fmt</c>, for <paramref name="encoder"/> at the
    /// options' codec, CRF, preset and tuning.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(StitchEncoder encoder, StitchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var codec = options.Codec;
        var hevc = codec == StitchCodec.Hevc;
        var tuned = options.HighQualityEncoderTuning;
        var name = encoder.FfmpegName(codec);
        var arguments = new List<string> { "-c:v", name };
        switch (encoder)
        {
            case StitchEncoder.Cpu:
                arguments.AddRange(["-preset", options.Preset, "-crf", Number(options.Crf)]);
                break;

            case StitchEncoder.Nvidia:
                // Constant-quality VBR: -cq sets the quality and -b:v 0 lifts the bitrate cap. NVENC takes yuv420p.
                arguments.AddRange(
                [
                    "-preset", tuned ? "p7" : NvencPreset(options.Preset), "-tune", "hq",
                    "-rc", "vbr", "-cq", Number(HardwareQuality(codec, options.Crf)), "-b:v", "0",
                ]);
                if (hevc)
                {
                    // With -b:v 0, hevc_nvenc still caps 4K at about 20 Mbit/s, which made every CQ below about 22
                    // give the same file (measured on an RTX 3080, ffmpeg 6.0). A high -maxrate lifts the cap.
                    arguments.AddRange(["-maxrate", HevcNvencMaxRate]);
                }

                if (tuned)
                {
                    arguments.AddRange(NvencTuningArguments);
                }

                break;

            case StitchEncoder.Intel:
                // Intelligent constant quality (ICQ).
                arguments.AddRange(["-preset", tuned ? "veryslow" : QsvPreset(options.Preset), "-global_quality", Number(HardwareQuality(codec, options.Crf))]);
                break;

            case StitchEncoder.Amd:
                var qp = Number(HardwareQuality(codec, options.Crf));
                arguments.AddRange(["-quality", tuned ? "quality" : AmfQuality(options.Preset), "-rc", "cqp", "-qp_i", qp, "-qp_p", qp]);
                if (!hevc)
                {
                    arguments.AddRange(["-qp_b", qp]);
                }

                break;

            case StitchEncoder.Apple:
                // Constant quality needs Apple silicon; -allow_sw lets macOS use its software encoder if it must.
                arguments.AddRange(["-q:v", Number(VideoToolboxQuality(options.Crf)), "-allow_sw", "1"]);
                break;
        }

        if (hevc)
        {
            // hvc1 tagging lets QuickTime and Apple devices play the file.
            arguments.AddRange(["-tag:v", "hvc1"]);
        }

        // nv12 and yuv420p are the same 8-bit 4:2:0 picture; Quick Sync, AMF and VideoToolbox prefer nv12.
        arguments.AddRange(["-pix_fmt", encoder is StitchEncoder.Cpu or StitchEncoder.Nvidia ? "yuv420p" : "nv12"]);
        return arguments;
    }

    /// <summary>The quality and preset as <paramref name="encoder"/> sees them, e.g. "CQ 21", "preset p7".</summary>
    internal static IReadOnlyList<string> DescribeQuality(StitchEncoder encoder, StitchOptions options)
    {
        var tuned = options.HighQualityEncoderTuning;
        var hw = HardwareQuality(options.Codec, options.Crf);
        return encoder switch
        {
            StitchEncoder.Cpu => [$"CRF {Number(options.Crf)}", $"preset {options.Preset}"],
            StitchEncoder.Nvidia => [$"CQ {Number(hw)} (CRF {Number(options.Crf)})", $"preset {(tuned ? "p7" : NvencPreset(options.Preset))}"],
            StitchEncoder.Intel => [$"ICQ {Number(hw)} (CRF {Number(options.Crf)})", $"preset {(tuned ? "veryslow" : QsvPreset(options.Preset))}"],
            StitchEncoder.Amd => [$"QP {Number(hw)} (CRF {Number(options.Crf)})", $"quality {(tuned ? "quality" : AmfQuality(options.Preset))}"],
            StitchEncoder.Apple => [$"q {Number(VideoToolboxQuality(options.Crf))} (CRF {Number(options.Crf)})"],
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Resolve Auto to a concrete encoder first."),
        };
    }

    /// <summary>Maps an x264 preset name to NVENC's p1 (fastest) to p7 (slowest).</summary>
    internal static string NvencPreset(string preset) => preset switch
    {
        "ultrafast" or "superfast" => "p1",
        "veryfast" => "p2",
        "faster" => "p3",
        "fast" => "p4",
        "slow" => "p6",
        "slower" or "veryslow" => "p7",
        _ => "p5",
    };

    /// <summary>Maps an x264 preset name to Quick Sync's, which start at veryfast.</summary>
    internal static string QsvPreset(string preset) => preset switch
    {
        "ultrafast" or "superfast" => "veryfast",
        "veryfast" or "faster" or "fast" or "slow" or "slower" or "veryslow" => preset,
        _ => "medium",
    };

    /// <summary>Maps an x264 preset name to AMF's speed/balanced/quality.</summary>
    internal static string AmfQuality(string preset) => preset switch
    {
        "ultrafast" or "superfast" or "veryfast" or "faster" => "speed",
        "slow" or "slower" or "veryslow" => "quality",
        _ => "balanced",
    };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
