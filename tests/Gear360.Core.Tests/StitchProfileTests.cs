using Gear360.Core.Stitching;

namespace Gear360.Core.Tests;

public class StitchProfileTests
{
    private static readonly MediaInfo Gear360Video = new(3840, 1920, TimeSpan.FromSeconds(10));

    [Theory]
    [InlineData(StitchProfile.Fast, StitchCodec.H264, 23, "veryfast", StitchInterpolation.Linear, false)]
    [InlineData(StitchProfile.Balanced, StitchCodec.H264, 20, "medium", StitchInterpolation.Linear, false)]
    [InlineData(StitchProfile.High, StitchCodec.H264, 16, "slow", StitchInterpolation.Linear, true)]
    [InlineData(StitchProfile.Max, StitchCodec.Hevc, 14, "slow", StitchInterpolation.Lanczos, true)]
    public void Each_profile_sets_its_quality_settings(StitchProfile profile, StitchCodec codec, int crf, string preset, StitchInterpolation interpolation, bool tuned)
    {
        var options = StitchProfiles.Get(profile);

        Assert.Equal(codec, options.Codec);
        Assert.Equal(crf, options.Crf);
        Assert.Equal(preset, options.Preset);
        Assert.Equal(interpolation, options.Interpolation);
        Assert.Equal(tuned, options.HighQualityEncoderTuning);
        Assert.Equal(StitchAudio.Copy, options.Audio);
        Assert.Equal(StitchEncoder.Auto, options.Encoder);
        options.Validate();
    }

    [Fact]
    public void Balanced_is_the_old_default()
    {
        Assert.Equal(new StitchOptions(), StitchProfiles.Get(StitchProfile.Balanced));
        Assert.Equal(StitchProfile.Balanced, StitchProfiles.Match(new StitchOptions()));
    }

    [Fact]
    public void A_profile_keeps_geometry_encoder_audio_and_output_settings()
    {
        var baseOptions = new StitchOptions
        {
            Fov = 193, Yaw = 10, Pitch = -5, Roll = 2, Encoder = StitchEncoder.Cpu, Audio = StitchAudio.Aac,
            OutputDirectory = "out", Overwrite = true, InjectMetadata = false, Crf = 40,
        };

        var max = StitchProfiles.ApplyTo(baseOptions, StitchProfile.Max);

        Assert.Equal(
            baseOptions with { Codec = StitchCodec.Hevc, Crf = 14, Preset = "slow", Interpolation = StitchInterpolation.Lanczos, HighQualityEncoderTuning = true },
            max);
        Assert.Same(baseOptions, StitchProfiles.ApplyTo(baseOptions, StitchProfile.Custom));
    }

    [Fact]
    public void Explicit_overrides_win_over_the_profile()
    {
        var overrides = new StitchOverrides
        {
            Crf = 18, Interpolation = StitchInterpolation.Lanczos, Encoder = StitchEncoder.Nvidia, Fov = 193, Audio = StitchAudio.Aac,
        };

        var options = StitchProfiles.Apply(StitchProfile.High, overrides, new StitchOptions { OutputDirectory = "out" });

        // Overridden.
        Assert.Equal(18, options.Crf);
        Assert.Equal(StitchInterpolation.Lanczos, options.Interpolation);
        Assert.Equal(StitchEncoder.Nvidia, options.Encoder);
        Assert.Equal(193, options.Fov);
        Assert.Equal(StitchAudio.Aac, options.Audio);

        // From the profile, or the base options.
        Assert.Equal(StitchCodec.H264, options.Codec);
        Assert.Equal("slow", options.Preset);
        Assert.True(options.HighQualityEncoderTuning);
        Assert.Equal("out", options.OutputDirectory);
        Assert.Equal(StitchProfile.Custom, StitchProfiles.Match(options));
    }

    [Fact]
    public void Overrides_can_switch_tuning_off_and_empty_overrides_change_nothing()
    {
        Assert.False(StitchProfiles.Apply(StitchProfile.Max, new StitchOverrides { HighQualityEncoderTuning = false }).HighQualityEncoderTuning);
        Assert.Equal(StitchProfiles.Get(StitchProfile.Max), StitchProfiles.Apply(StitchProfile.Max, new StitchOverrides()));
        Assert.Equal(StitchProfiles.Get(StitchProfile.Fast), StitchProfiles.Apply(StitchProfile.Fast));
    }

    [Fact]
    public void Match_finds_each_named_profile_and_custom_otherwise()
    {
        foreach (var profile in StitchProfiles.Named)
        {
            Assert.Equal(profile, StitchProfiles.Match(StitchProfiles.Get(profile) with { Fov = 190, Encoder = StitchEncoder.Cpu }));
        }

        Assert.Equal(StitchProfile.Custom, StitchProfiles.Match(StitchProfiles.Get(StitchProfile.High) with { Preset = "medium" }));
        Assert.Equal(StitchProfile.Custom, StitchProfiles.Match(new StitchOptions { Crf = 19 }));
    }

    [Fact]
    public void Every_profile_has_a_description_and_cli_name()
    {
        foreach (var profile in Enum.GetValues<StitchProfile>())
        {
            Assert.False(string.IsNullOrWhiteSpace(StitchProfiles.Describe(profile)));
        }

        Assert.Equal("high", StitchProfile.High.CliName());
    }

    // ---- Arguments per profile and encoder ----

    [Theory]
    [InlineData(StitchProfile.Fast, StitchEncoder.Cpu, "libx264 -preset veryfast -crf 23 -pix_fmt yuv420p")]
    [InlineData(StitchProfile.Balanced, StitchEncoder.Cpu, "libx264 -preset medium -crf 20 -pix_fmt yuv420p")]
    [InlineData(StitchProfile.High, StitchEncoder.Cpu, "libx264 -preset slow -crf 16 -pix_fmt yuv420p")]
    [InlineData(StitchProfile.Max, StitchEncoder.Cpu, "libx265 -preset slow -crf 14 -tag:v hvc1 -pix_fmt yuv420p")]
    [InlineData(StitchProfile.Fast, StitchEncoder.Nvidia, "h264_nvenc -preset p2 -tune hq -rc vbr -cq 28 -b:v 0 -pix_fmt yuv420p")]
    [InlineData(StitchProfile.Balanced, StitchEncoder.Nvidia, "h264_nvenc -preset p5 -tune hq -rc vbr -cq 25 -b:v 0 -pix_fmt yuv420p")]
    [InlineData(StitchProfile.High, StitchEncoder.Nvidia, "h264_nvenc -preset p7 -tune hq -rc vbr -cq 21 -b:v 0 -multipass fullres -spatial_aq 1 -temporal_aq 1 -rc-lookahead 32 -bf 3 -b_ref_mode middle -pix_fmt yuv420p")]
    [InlineData(StitchProfile.Max, StitchEncoder.Nvidia, "hevc_nvenc -preset p7 -tune hq -rc vbr -cq 21 -b:v 0 -maxrate 400M -multipass fullres -spatial_aq 1 -temporal_aq 1 -rc-lookahead 32 -bf 3 -b_ref_mode middle -tag:v hvc1 -pix_fmt yuv420p")]
    [InlineData(StitchProfile.Balanced, StitchEncoder.Intel, "h264_qsv -preset medium -global_quality 25 -pix_fmt nv12")]
    [InlineData(StitchProfile.High, StitchEncoder.Intel, "h264_qsv -preset veryslow -global_quality 21 -pix_fmt nv12")]
    [InlineData(StitchProfile.Balanced, StitchEncoder.Amd, "h264_amf -quality balanced -rc cqp -qp_i 25 -qp_p 25 -qp_b 25 -pix_fmt nv12")]
    [InlineData(StitchProfile.High, StitchEncoder.Amd, "h264_amf -quality quality -rc cqp -qp_i 21 -qp_p 21 -qp_b 21 -pix_fmt nv12")]
    [InlineData(StitchProfile.High, StitchEncoder.Apple, "h264_videotoolbox -q:v 68 -allow_sw 1 -pix_fmt nv12")]
    [InlineData(StitchProfile.Max, StitchEncoder.Apple, "hevc_videotoolbox -q:v 72 -allow_sw 1 -tag:v hvc1 -pix_fmt nv12")]
    public void Encoder_arguments_for_each_profile(StitchProfile profile, StitchEncoder encoder, string expected)
    {
        var options = StitchProfiles.Get(profile);

        Assert.Equal("-c:v " + expected, string.Join(' ', StitchEncoders.BuildArguments(encoder, options)));
    }

    [Theory]
    [InlineData(StitchEncoder.Cpu, false)]
    [InlineData(StitchEncoder.Nvidia, true)]
    [InlineData(StitchEncoder.Intel, true)]
    [InlineData(StitchEncoder.Amd, true)]
    [InlineData(StitchEncoder.Apple, false)]
    public void Only_some_encoders_have_hq_tuning(StitchEncoder encoder, bool tunable)
    {
        Assert.Equal(tunable, StitchEncoders.HasHighQualityTuning(encoder));
        var plain = StitchEncoders.BuildArguments(encoder, new StitchOptions());
        var tuned = StitchEncoders.BuildArguments(encoder, new StitchOptions { HighQualityEncoderTuning = true });
        Assert.Equal(tunable, !plain.SequenceEqual(tuned));
        Assert.Equal(encoder == StitchEncoder.Nvidia, tuned.Contains("-multipass"));
    }

    [Theory]
    [InlineData(StitchProfile.Fast, "")]
    [InlineData(StitchProfile.Balanced, "")]
    [InlineData(StitchProfile.High, "")]
    [InlineData(StitchProfile.Max, ":interp=lanczos")]
    public void Interpolation_goes_into_the_filter_when_it_is_not_bilinear(StitchProfile profile, string suffix)
    {
        var args = Stitcher.BuildArguments("in.mp4", "out", Gear360Video, StitchProfiles.Get(profile), MediaKind.Video, StitchEncoder.Nvidia);

        Assert.Equal("v360=input=dfisheye:output=e:ih_fov=195:iv_fov=195:w=3840:h=1920" + suffix, args[args.ToList().IndexOf("-vf") + 1]);
    }

    [Theory]
    [InlineData(StitchInterpolation.Linear, "line")]
    [InlineData(StitchInterpolation.Cubic, "cubic")]
    [InlineData(StitchInterpolation.Lanczos, "lanczos")]
    [InlineData(StitchInterpolation.Spline16, "spline16")]
    [InlineData(StitchInterpolation.Gaussian, "gauss")]
    [InlineData(StitchInterpolation.Mitchell, "mitchell")]
    [InlineData(StitchInterpolation.Lagrange9, "lagrange9")]
    [InlineData(StitchInterpolation.Nearest, "near")]
    public void Interpolation_names_are_v360s(StitchInterpolation interpolation, string name)
    {
        Assert.Equal(name, interpolation.FfmpegName());
        var filter = Stitcher.BuildFilter(Gear360Video, new StitchOptions { Interpolation = interpolation, Roll = 1 });
        Assert.Equal(interpolation != StitchInterpolation.Linear, filter.EndsWith(":roll=1:interp=" + name, StringComparison.Ordinal));
    }

    [Fact]
    public void Audio_is_copied_or_reencoded_to_aac()
    {
        var copy = string.Join(' ', Stitcher.BuildArguments("in.mp4", "out", Gear360Video, new StitchOptions(), MediaKind.Video));
        var aac = string.Join(' ', Stitcher.BuildArguments("in.mp4", "out", Gear360Video, new StitchOptions { Audio = StitchAudio.Aac }, MediaKind.Video));

        Assert.Contains("-c:a copy", copy, StringComparison.Ordinal);
        Assert.Contains("-c:a aac -b:a 192k", aac, StringComparison.Ordinal);
        Assert.DoesNotContain("-c:a copy", aac, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_interpolation_or_audio_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StitchOptions { Interpolation = (StitchInterpolation)99 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StitchOptions { Audio = (StitchAudio)99 }.Validate());
    }

    [Fact]
    public void Summary_shows_the_effective_settings_for_the_encoder()
    {
        Assert.Equal(
            "h264_nvenc, CQ 21 (CRF 16), preset p7, HQ tuning, interp line, audio copy, FOV 195",
            StitchProfiles.Summarize(StitchProfiles.Get(StitchProfile.High), StitchEncoder.Nvidia));
        Assert.Equal(
            "libx265, CRF 14, preset slow, interp lanczos, audio AAC 192k, FOV 193.5, yaw/pitch/roll 90/0/-1.5",
            StitchProfiles.Summarize(StitchProfiles.Get(StitchProfile.Max) with { Audio = StitchAudio.Aac, Fov = 193.5, Yaw = 90, Roll = -1.5 }, StitchEncoder.Cpu));
    }
}
