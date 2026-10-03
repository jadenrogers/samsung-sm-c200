using System.Globalization;
using Gear360.Core.Stitching;

namespace Gear360.Core.Tests;

public class StitchEncoderTests
{
    private const string EncoderList = """
        Encoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         .F.... = Frame-level multithreading
         ------
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D h264_amf             AMD AMF H.264 Encoder (codec h264)
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         V..... h264_qsv             H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (Intel Quick Sync Video acceleration) (codec h264)
         V....D libx265              libx265 H.265 / HEVC (codec hevc)
         V....D hevc_nvenc           NVIDIA NVENC hevc encoder (codec hevc)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    private static readonly MediaInfo Gear360Video = new(3840, 1920, TimeSpan.FromSeconds(10));

    // ---- Arguments ----

    [Theory]
    [InlineData(StitchEncoder.Cpu, StitchCodec.H264, "libx264 -preset medium -crf 20 -pix_fmt yuv420p")]
    [InlineData(StitchEncoder.Cpu, StitchCodec.Hevc, "libx265 -preset medium -crf 20 -tag:v hvc1 -pix_fmt yuv420p")]
    [InlineData(StitchEncoder.Nvidia, StitchCodec.H264, "h264_nvenc -preset p5 -tune hq -rc vbr -cq 25 -b:v 0 -pix_fmt yuv420p")]
    [InlineData(StitchEncoder.Nvidia, StitchCodec.Hevc, "hevc_nvenc -preset p5 -tune hq -rc vbr -cq 27 -b:v 0 -maxrate 400M -tag:v hvc1 -pix_fmt yuv420p")]
    [InlineData(StitchEncoder.Intel, StitchCodec.H264, "h264_qsv -preset medium -global_quality 25 -pix_fmt nv12")]
    [InlineData(StitchEncoder.Intel, StitchCodec.Hevc, "hevc_qsv -preset medium -global_quality 27 -tag:v hvc1 -pix_fmt nv12")]
    [InlineData(StitchEncoder.Amd, StitchCodec.H264, "h264_amf -quality balanced -rc cqp -qp_i 25 -qp_p 25 -qp_b 25 -pix_fmt nv12")]
    [InlineData(StitchEncoder.Amd, StitchCodec.Hevc, "hevc_amf -quality balanced -rc cqp -qp_i 27 -qp_p 27 -tag:v hvc1 -pix_fmt nv12")]
    [InlineData(StitchEncoder.Apple, StitchCodec.H264, "h264_videotoolbox -q:v 60 -allow_sw 1 -pix_fmt nv12")]
    [InlineData(StitchEncoder.Apple, StitchCodec.Hevc, "hevc_videotoolbox -q:v 60 -allow_sw 1 -tag:v hvc1 -pix_fmt nv12")]
    public void Encoder_arguments_for_each_encoder_and_codec(StitchEncoder encoder, StitchCodec codec, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
        try
        {
            var args = StitchEncoders.BuildArguments(encoder, new StitchOptions { Codec = codec });
            Assert.Equal("-c:v " + expected, string.Join(' ', args));

            // The same arguments land in the full command line, which still ends with audio copy and the output.
            var full = Stitcher.BuildArguments("in.mp4", "out.part", Gear360Video, new StitchOptions { Codec = codec }, MediaKind.Video, encoder);
            Assert.Contains(string.Join(' ', args), string.Join(' ', full), StringComparison.Ordinal);
            Assert.Equal("out.part", full[^1]);
            Assert.Contains("-c:a", full);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Auto_must_be_resolved_before_building_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StitchEncoders.BuildArguments(StitchEncoder.Auto, new StitchOptions()));
    }

    [Theory]
    [InlineData(StitchCodec.H264, 20, 25)]
    [InlineData(StitchCodec.Hevc, 20, 27)]
    [InlineData(StitchCodec.H264, 0, 5)]
    [InlineData(StitchCodec.H264, 51, 51)]
    [InlineData(StitchCodec.Hevc, 48, 51)]
    public void Crf_maps_to_the_hardware_quantizer(StitchCodec codec, int crf, int expected)
    {
        Assert.Equal(expected, StitchEncoders.HardwareQuality(codec, crf));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(20, 60)]
    [InlineData(28, 44)]
    [InlineData(51, 1)]
    public void Crf_maps_inversely_to_videotoolbox_quality(int crf, int expected)
    {
        Assert.Equal(expected, StitchEncoders.VideoToolboxQuality(crf));
    }

    [Theory]
    [InlineData("ultrafast", "p1", "veryfast", "speed")]
    [InlineData("veryfast", "p2", "veryfast", "speed")]
    [InlineData("fast", "p4", "fast", "balanced")]
    [InlineData("medium", "p5", "medium", "balanced")]
    [InlineData("slow", "p6", "slow", "quality")]
    [InlineData("veryslow", "p7", "veryslow", "quality")]
    public void Presets_map_to_the_nearest_hardware_preset(string preset, string nvenc, string qsv, string amf)
    {
        Assert.Equal(nvenc, StitchEncoders.NvencPreset(preset));
        Assert.Equal(qsv, StitchEncoders.QsvPreset(preset));
        Assert.Equal(amf, StitchEncoders.AmfQuality(preset));
    }

    // ---- Detection ----

    [Fact]
    public void Encoder_list_is_parsed()
    {
        var names = EncoderDetector.ParseEncoderList(EncoderList.ReplaceLineEndings("\r\n"));

        Assert.Contains("h264_nvenc", names);
        Assert.Contains("h264_qsv", names);
        Assert.Contains("libx265", names);
        Assert.DoesNotContain("aac", names);
        Assert.DoesNotContain("V.....", names);
    }

    [Fact]
    public async Task Auto_picks_the_first_candidate_whose_test_encode_works_and_caches_it()
    {
        var runner = new FakeRunner { Working = { "h264_amf" } };
        var detector = new EncoderDetector(runner.RunAsync, [StitchEncoder.Nvidia, StitchEncoder.Intel, StitchEncoder.Amd]);

        Assert.Equal(StitchEncoder.Amd, await detector.ResolveAsync("ffmpeg", StitchEncoder.Auto, StitchCodec.H264));

        // Listed and probed in order: Nvidia and Intel failed their test encodes before AMD worked.
        Assert.Equal(["-encoders", "h264_nvenc", "h264_qsv", "h264_amf"], runner.Calls);

        Assert.Equal(StitchEncoder.Amd, await detector.ResolveAsync("ffmpeg", StitchEncoder.Auto, StitchCodec.H264));
        Assert.Equal(4, runner.Calls.Count);
    }

    [Fact]
    public async Task Auto_falls_back_to_the_cpu_when_no_hardware_encoder_works()
    {
        // hevc_amf and hevc_qsv are not in the list, so only hevc_nvenc is test-encoded (and fails).
        var runner = new FakeRunner();
        var detector = new EncoderDetector(runner.RunAsync, [StitchEncoder.Nvidia, StitchEncoder.Intel, StitchEncoder.Amd]);

        Assert.Equal(StitchEncoder.Cpu, await detector.ResolveAsync("ffmpeg", StitchEncoder.Auto, StitchCodec.Hevc));
        Assert.Equal(["-encoders", "hevc_nvenc"], runner.Calls);
    }

    [Fact]
    public async Task An_ffmpeg_that_cannot_run_means_cpu()
    {
        var runner = new FakeRunner { ListFails = true };
        var detector = new EncoderDetector(runner.RunAsync, [StitchEncoder.Nvidia]);

        Assert.Equal(StitchEncoder.Cpu, await detector.DetectAsync("ffmpeg", StitchCodec.H264));
    }

    [Fact]
    public async Task Explicit_encoders_are_checked_and_unusable_ones_are_an_error()
    {
        var runner = new FakeRunner { Working = { "h264_nvenc" } };
        var detector = new EncoderDetector(runner.RunAsync, [StitchEncoder.Nvidia]);

        Assert.Equal(StitchEncoder.Cpu, await detector.ResolveAsync("ffmpeg", StitchEncoder.Cpu, StitchCodec.H264));
        Assert.Empty(runner.Calls);
        Assert.Equal(StitchEncoder.Nvidia, await detector.ResolveAsync("ffmpeg", StitchEncoder.Nvidia, StitchCodec.H264));

        var failedProbe = await Assert.ThrowsAsync<StitchException>(() => detector.ResolveAsync("ffmpeg", StitchEncoder.Intel, StitchCodec.H264));
        Assert.Contains("Intel Quick Sync", failedProbe.Message);
        Assert.Contains("test encode with h264_qsv failed", failedProbe.Message);

        var notBuilt = await Assert.ThrowsAsync<StitchException>(() => detector.ResolveAsync("ffmpeg", StitchEncoder.Apple, StitchCodec.H264));
        Assert.Contains("built without h264_videotoolbox", notBuilt.Message);

        var usable = await detector.ListUsableAsync("ffmpeg", StitchCodec.H264);
        Assert.Equal([StitchEncoder.Nvidia], usable);
    }

    [Fact]
    public void Probe_encodes_a_tiny_test_picture_with_the_stitch_settings()
    {
        var args = EncoderDetector.BuildProbeArguments(StitchEncoder.Nvidia, StitchCodec.Hevc);
        Assert.Equal(
            "-hide_banner -nostdin -v error -f lavfi -i color=size=256x256:duration=0.1 -c:v hevc_nvenc -preset p5 -tune hq -rc vbr -cq 27 -b:v 0 -maxrate 400M -tag:v hvc1 -pix_fmt yuv420p -f null -",
            string.Join(' ', args));
    }

    // ---- Fallback while stitching ----

    [Fact]
    public async Task Auto_retries_a_file_on_the_cpu_when_the_hardware_encode_fails()
    {
        using var temp = new TempDirectory();
        var input = temp.CreateFile("SAM_0001.MP4", 10);
        var runner = new FakeRunner { Working = { "h264_nvenc" }, FailWhileStitching = { "h264_nvenc" } };
        var stitcher = new Stitcher(new FfmpegTools("ffmpeg", "ffprobe"), new EncoderDetector(runner.RunAsync, [StitchEncoder.Nvidia]), runner.RunAsync);
        var reports = new List<double>();

        var result = await stitcher.StitchAsync(input, new StitchOptions { InjectMetadata = false }, new ListProgress(reports));

        Assert.Equal(StitchEncoder.Cpu, result.Encoder);
        Assert.NotNull(result.FallbackReason);
        Assert.Contains("NVIDIA NVENC failed (exit code 1: No NVENC capable devices found)", result.FallbackReason);
        Assert.Equal(["stitch h264_nvenc", "stitch libx264"], runner.Calls.Where(c => c.StartsWith("stitch", StringComparison.Ordinal)));
        Assert.Equal(["SAM_0001.MP4", "SAM_0001_stitched.mp4"], temp.ListFiles());
        Assert.Equal(1.0, reports[^1]);
    }

    [Fact]
    public async Task A_working_hardware_encode_is_reported_and_not_retried()
    {
        using var temp = new TempDirectory();
        var input = temp.CreateFile("SAM_0001.MP4", 10);
        var runner = new FakeRunner { Working = { "h264_nvenc" } };
        var stitcher = new Stitcher(new FfmpegTools("ffmpeg", "ffprobe"), new EncoderDetector(runner.RunAsync, [StitchEncoder.Nvidia]), runner.RunAsync);

        var result = await stitcher.StitchAsync(input, new StitchOptions { InjectMetadata = false });

        Assert.Equal(StitchEncoder.Nvidia, result.Encoder);
        Assert.Null(result.FallbackReason);
        Assert.Equal(["stitch h264_nvenc"], runner.Calls.Where(c => c.StartsWith("stitch", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_explicit_hardware_encoder_that_fails_is_not_retried()
    {
        using var temp = new TempDirectory();
        var input = temp.CreateFile("SAM_0001.MP4", 10);
        var runner = new FakeRunner { Working = { "h264_nvenc" }, FailWhileStitching = { "h264_nvenc" } };
        var stitcher = new Stitcher(new FfmpegTools("ffmpeg", "ffprobe"), new EncoderDetector(runner.RunAsync, [StitchEncoder.Nvidia]), runner.RunAsync);

        var ex = await Assert.ThrowsAsync<StitchException>(
            () => stitcher.StitchAsync(input, new StitchOptions { Encoder = StitchEncoder.Nvidia, InjectMetadata = false }));

        Assert.Contains("No NVENC capable devices found", ex.Message);
        Assert.Equal(["stitch h264_nvenc"], runner.Calls.Where(c => c.StartsWith("stitch", StringComparison.Ordinal)));
        Assert.Equal(["SAM_0001.MP4"], temp.ListFiles());
    }

    [Fact]
    public async Task An_explicit_encoder_that_is_not_usable_fails_before_ffmpeg_runs()
    {
        using var temp = new TempDirectory();
        var input = temp.CreateFile("SAM_0001.MP4", 10);
        var runner = new FakeRunner();
        var stitcher = new Stitcher(new FfmpegTools("ffmpeg", "ffprobe"), new EncoderDetector(runner.RunAsync, [StitchEncoder.Nvidia]), runner.RunAsync);

        await Assert.ThrowsAsync<StitchException>(
            () => stitcher.StitchAsync(input, new StitchOptions { Encoder = StitchEncoder.Nvidia, InjectMetadata = false }));

        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("stitch", StringComparison.Ordinal));
        Assert.Equal(["SAM_0001.MP4"], temp.ListFiles());
    }

    // ---- High-quality tuning ----

    [Fact]
    public async Task Hq_tuning_is_used_when_the_card_supports_it()
    {
        using var temp = new TempDirectory();
        var input = temp.CreateFile("SAM_0001.MP4", 10);
        var runner = new FakeRunner { Working = { "h264_nvenc" } };
        var stitcher = new Stitcher(new FfmpegTools("ffmpeg", "ffprobe"), new EncoderDetector(runner.RunAsync, [StitchEncoder.Nvidia]), runner.RunAsync);

        var result = await stitcher.StitchAsync(input, StitchProfiles.Get(StitchProfile.High) with { InjectMetadata = false });

        Assert.False(result.TuningSkipped);
        Assert.Equal(["-encoders", "h264_nvenc", "h264_nvenc tuned", "stitch h264_nvenc tuned"], runner.Calls);
    }

    [Fact]
    public async Task Hq_tuning_is_dropped_on_a_card_that_rejects_it_and_the_check_is_cached()
    {
        using var temp = new TempDirectory();
        var first = temp.CreateFile("SAM_0001.MP4", 10);
        var second = temp.CreateFile("SAM_0002.MP4", 10);
        var runner = new FakeRunner { Working = { "h264_nvenc" }, NoTuning = { "h264_nvenc" } };
        var stitcher = new Stitcher(new FfmpegTools("ffmpeg", "ffprobe"), new EncoderDetector(runner.RunAsync, [StitchEncoder.Nvidia]), runner.RunAsync);
        var options = StitchProfiles.Get(StitchProfile.High) with { InjectMetadata = false };

        var result = await stitcher.StitchAsync(first, options);
        await stitcher.StitchAsync(second, options);

        Assert.True(result.TuningSkipped);
        Assert.Equal(StitchEncoder.Nvidia, result.Encoder);
        Assert.Equal(["-encoders", "h264_nvenc", "h264_nvenc tuned", "stitch h264_nvenc", "stitch h264_nvenc"], runner.Calls);
    }

    [Fact]
    public async Task Hq_tuning_is_not_checked_for_the_cpu()
    {
        using var temp = new TempDirectory();
        var input = temp.CreateFile("SAM_0001.MP4", 10);
        var runner = new FakeRunner();
        var stitcher = new Stitcher(new FfmpegTools("ffmpeg", "ffprobe"), new EncoderDetector(runner.RunAsync, [StitchEncoder.Nvidia]), runner.RunAsync);

        var result = await stitcher.StitchAsync(input, StitchProfiles.Get(StitchProfile.High) with { Encoder = StitchEncoder.Cpu, InjectMetadata = false });

        Assert.False(result.TuningSkipped);
        Assert.Equal(["stitch libx264"], runner.Calls);
    }

    [Fact]
    public void Tuned_probe_uses_the_tuning_flags()
    {
        var args = string.Join(' ', EncoderDetector.BuildProbeArguments(StitchEncoder.Nvidia, StitchCodec.H264, tuned: true));
        Assert.Contains("-preset p7", args, StringComparison.Ordinal);
        Assert.Contains("-multipass fullres", args, StringComparison.Ordinal);
        Assert.Contains("-b_ref_mode middle", args, StringComparison.Ordinal);
    }

    // ---- Real hardware ----

    [SkippableFact]
    public async Task Stitches_a_synthetic_clip_with_nvenc_when_this_machine_has_it()
    {
        var tools = Ffmpeg.Require();
        Skip.IfNot(
            await EncoderDetector.Shared.IsUsableAsync(tools.FfmpegPath, StitchEncoder.Nvidia, StitchCodec.H264),
            "NVENC is not usable on this machine");
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "SAM_0001.MP4");
        Ffmpeg.CreateClip(input, 1920, 960, 1, faststart: false);

        var result = await new Stitcher(tools).StitchAsync(input, new StitchOptions { Encoder = StitchEncoder.Nvidia });

        Assert.Equal(StitchEncoder.Nvidia, result.Encoder);
        Assert.Null(result.FallbackReason);
        var (width, height, _) = Ffmpeg.Probe(result.OutputPath);
        Assert.Equal((1920, 960), (width, height));
        Assert.Contains("Spherical Mapping", Ffmpeg.ProbeVideoStream(result.OutputPath));
        Assert.Contains("\"codec_name\": \"h264\"", Ffmpeg.ProbeVideoStream(result.OutputPath));
    }

    /// <summary>A fake ffmpeg/ffprobe: lists <see cref="EncoderList"/>, passes test encodes for <see cref="Working"/>.</summary>
    private sealed class FakeRunner
    {
        public HashSet<string> Working { get; } = [];

        public HashSet<string> FailWhileStitching { get; } = [];

        /// <summary>Encoders whose test encode works, but not with high-quality tuning.</summary>
        public HashSet<string> NoTuning { get; } = [];

        public bool ListFails { get; init; }

        /// <summary>"-encoders", the encoder name of each test encode, or "stitch &lt;encoder&gt;".</summary>
        public List<string> Calls { get; } = [];

        public Task<ToolResult> RunAsync(string fileName, IReadOnlyList<string> arguments, Action<string>? onOutputLine, CancellationToken cancellationToken)
        {
            if (fileName == "ffprobe")
            {
                return Done(0, """{ "streams": [ { "width": 1920, "height": 960 } ], "format": { "duration": "1.0" } }""");
            }

            if (arguments.Contains("-encoders"))
            {
                Calls.Add("-encoders");
                return ListFails ? Task.FromException<ToolResult>(new System.ComponentModel.Win32Exception("cannot start")) : Done(0, EncoderList);
            }

            var encoder = arguments[arguments.ToList().IndexOf("-c:v") + 1];
            var tuned = arguments.Contains("-multipass");
            if (arguments.Contains("lavfi"))
            {
                Calls.Add(tuned ? encoder + " tuned" : encoder);
                return Done(Working.Contains(encoder) && !(tuned && NoTuning.Contains(encoder)) ? 0 : 1, string.Empty);
            }

            Calls.Add("stitch " + encoder + (tuned ? " tuned" : string.Empty));
            if (FailWhileStitching.Contains(encoder))
            {
                File.WriteAllText(arguments[^1], "half a file");
                return Task.FromResult(new ToolResult(1, string.Empty, "[h264_nvenc @ 0] OpenEncodeSessionEx failed\nNo NVENC capable devices found"));
            }

            onOutputLine?.Invoke("out_time_us=500000");
            File.WriteAllText(arguments[^1], "stitched");
            return Done(0, string.Empty);
        }

        private static Task<ToolResult> Done(int exitCode, string output) => Task.FromResult(new ToolResult(exitCode, output, string.Empty));
    }

    private sealed class ListProgress(List<double> reports) : IProgress<double>
    {
        public void Report(double value) => reports.Add(value);
    }
}
