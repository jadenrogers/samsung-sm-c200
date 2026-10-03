using System.Globalization;
using Gear360.Core.Stitching;

namespace Gear360.Core.Tests;

public class StitcherTests
{
    private static readonly MediaInfo Gear360Video = new(3840, 1920, TimeSpan.FromSeconds(10));

    [Fact]
    public void Default_arguments_map_dual_fisheye_to_2_to_1_equirectangular()
    {
        var args = Stitcher.BuildArguments("in.mp4", "out.mp4.part", Gear360Video, new StitchOptions(), MediaKind.Video);

        AssertPair(args, "-i", "in.mp4");
        AssertPair(args, "-vf", "v360=input=dfisheye:output=e:ih_fov=195:iv_fov=195:w=3840:h=1920");
        AssertPair(args, "-c:v", "libx264");
        AssertPair(args, "-crf", "20");
        AssertPair(args, "-preset", "medium");
        AssertPair(args, "-c:a", "copy");
        AssertPair(args, "-movflags", "+faststart");
        AssertPair(args, "-progress", "pipe:1");
        AssertPair(args, "-f", "mp4");
        Assert.Contains("-nostdin", args);
        Assert.Equal("out.mp4.part", args[^1]);
    }

    [Fact]
    public void Geometry_and_codec_options_are_passed_through_with_invariant_numbers()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var options = new StitchOptions { Fov = 193.5, Yaw = 90, Pitch = -2.25, Roll = 1, Codec = StitchCodec.Hevc, Crf = 24, Preset = "slow" };
            var args = Stitcher.BuildArguments("in.mp4", "out", new MediaInfo(2560, 1280, null), options, MediaKind.Video);

            AssertPair(args, "-vf", "v360=input=dfisheye:output=e:ih_fov=193.5:iv_fov=193.5:w=2560:h=1280:yaw=90:pitch=-2.25:roll=1");
            AssertPair(args, "-c:v", "libx265");
            AssertPair(args, "-tag:v", "hvc1");
            AssertPair(args, "-crf", "24");
            AssertPair(args, "-preset", "slow");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Output_size_is_even_and_2_to_1_for_odd_inputs()
    {
        var filter = Stitcher.BuildFilter(new MediaInfo(1922, 961, null), new StitchOptions());
        Assert.EndsWith(":w=1920:h=960", filter);
    }

    [Fact]
    public void Photos_are_written_as_a_single_jpeg_frame()
    {
        var args = Stitcher.BuildArguments("in.jpg", "out.jpg.part", new MediaInfo(7776, 3888, null), new StitchOptions(), MediaKind.Photo);

        AssertPair(args, "-c:v", "mjpeg");
        AssertPair(args, "-frames:v", "1");
        AssertPair(args, "-f", "image2");
        Assert.DoesNotContain("-movflags", args);
    }

    [Fact]
    public void Output_path_is_name_stitched_next_to_input_or_in_output_directory()
    {
        var input = Path.Combine(Path.GetTempPath(), "DCIM", "SAM_0001.MP4");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "DCIM", "SAM_0001_stitched.mp4"), Stitcher.GetOutputPath(input, new StitchOptions()));

        var outDir = Path.Combine(Path.GetTempPath(), "out");
        Assert.Equal(Path.Combine(outDir, "SAM_0001_stitched.mp4"), Stitcher.GetOutputPath(input, new StitchOptions { OutputDirectory = outDir }));
        Assert.Equal(Path.Combine(outDir, "SAM_0002_stitched.jpg"), Stitcher.GetOutputPath("SAM_0002.JPG", new StitchOptions { OutputDirectory = outDir }));

        Assert.True(Stitcher.IsStitchedOutput("SAM_0001_stitched.mp4"));
        Assert.False(Stitcher.IsStitchedOutput(input));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    [InlineData(double.NaN)]
    public void Invalid_fov_is_rejected(double fov)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StitchOptions { Fov = fov }.Validate());
    }

    [Fact]
    public void Invalid_crf_and_angles_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StitchOptions { Crf = 52 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StitchOptions { Yaw = 181 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StitchOptions { Preset = "fast; rm" }.Validate());
        new StitchOptions().Validate();
    }

    [Fact]
    public void Progress_lines_are_turned_into_fractions()
    {
        var reports = new List<double>();
        var parser = Stitcher.ProgressParser(TimeSpan.FromSeconds(10), new InlineProgress(reports.Add))!;

        parser("frame=10");
        parser("out_time_us=2500000");
        parser("out_time_ms=5000000");
        parser("out_time_us=N/A");
        parser("out_time=00:00:05.000000");
        parser("out_time_us=99000000");

        Assert.Equal([0.25, 0.5, 1.0], reports);
        Assert.Null(Stitcher.ProgressParser(null, new InlineProgress(reports.Add)));
    }

    [Fact]
    public void Probe_output_is_parsed()
    {
        const string json = """{ "programs": [], "streams": [ { "width": 3840, "height": 1920 } ], "format": { "duration": "12.345000" } }""";
        var info = Stitcher.ParseProbeOutput(json);
        Assert.Equal(new MediaInfo(3840, 1920, TimeSpan.FromSeconds(12.345)), info);

        var photo = Stitcher.ParseProbeOutput("""{ "streams": [ { "width": 10, "height": 5 } ], "format": { "duration": "N/A" } }""");
        Assert.Null(photo.Duration);
    }

    [Fact]
    public void Locator_reports_a_missing_explicit_path()
    {
        var ex = Assert.Throws<FfmpegNotFoundException>(() => FfmpegLocator.Locate(Path.Combine(Path.GetTempPath(), "no-such-ffmpeg-" + Guid.NewGuid())));
        Assert.Contains("not found", ex.Message);
    }

    [SkippableFact]
    public void Locator_accepts_the_ffmpeg_folder()
    {
        var tools = Ffmpeg.Require();
        var found = FfmpegLocator.Locate(Path.GetDirectoryName(tools.FfmpegPath));
        Assert.Equal(tools.FfmpegPath, found.FfmpegPath, ignoreCase: true);
        Assert.True(File.Exists(found.FfprobePath));
    }

    // ---- End to end with ffmpeg ----

    [SkippableFact]
    public async Task Stitches_a_synthetic_dual_fisheye_clip_to_a_360_video()
    {
        var tools = Ffmpeg.Require();
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "SAM_0001.MP4");
        Ffmpeg.CreateClip(input, 1920, 960, 1, faststart: false);
        var options = new StitchOptions { Preset = "ultrafast" };
        var reports = new List<double>();

        var result = await new Stitcher(tools).StitchAsync(input, options, new InlineProgress(reports.Add));

        Assert.False(result.Skipped);
        Assert.Equal(Path.Combine(temp.Path, "SAM_0001_stitched.mp4"), result.OutputPath);
        var (width, height, duration) = Ffmpeg.Probe(result.OutputPath);
        Assert.Equal((1920, 960), (width, height));
        Assert.InRange(duration, 0.9, 1.2);
        Assert.True(SphericalMetadataInjector.HasSphericalMetadata(result.OutputPath));
        Assert.Contains("Spherical Mapping", Ffmpeg.ProbeVideoStream(result.OutputPath));
        Assert.Equal(1.0, reports[^1]);
        Assert.Equal(["SAM_0001.MP4", "SAM_0001_stitched.mp4"], temp.ListFiles());

        // A second run leaves the existing output alone unless asked to overwrite.
        var again = await new Stitcher(tools).StitchAsync(input, options);
        Assert.True(again.Skipped);
        var overwritten = await new Stitcher(tools).StitchAsync(input, options with { Overwrite = true });
        Assert.False(overwritten.Skipped);
    }

    [SkippableFact]
    public async Task Stitches_a_photo_into_a_2_to_1_jpeg()
    {
        var tools = Ffmpeg.Require();
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "SAM_0002.JPG");
        Ffmpeg.CreatePhoto(input, 1920, 960);
        var outDir = Path.Combine(temp.Path, "out");

        var result = await new Stitcher(tools).StitchAsync(input, new StitchOptions { OutputDirectory = outDir });

        Assert.Equal(Path.Combine(outDir, "SAM_0002_stitched.jpg"), result.OutputPath);
        var (width, height, _) = Ffmpeg.Probe(result.OutputPath);
        Assert.Equal((1920, 960), (width, height));
    }

    [SkippableFact]
    public async Task Cancelling_stops_ffmpeg_and_removes_the_partial_file()
    {
        var tools = Ffmpeg.Require();
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "long.mp4");
        Ffmpeg.CreateClip(input, 1920, 960, 30, faststart: false);
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Stitcher(tools).StitchAsync(input, new StitchOptions { Preset = "veryslow" }, progress, cts.Token));

        Assert.Equal(["long.mp4"], temp.ListFiles());
    }

    [SkippableFact]
    public async Task Ffmpeg_failures_are_reported_and_leave_no_output()
    {
        var tools = Ffmpeg.Require();
        using var temp = new TempDirectory();
        var input = temp.CreateFile("broken.mp4", 4096);

        var ex = await Assert.ThrowsAsync<StitchException>(() => new Stitcher(tools).StitchAsync(input, new StitchOptions()));

        Assert.Contains("broken.mp4", ex.Message);
        Assert.Equal(["broken.mp4"], temp.ListFiles());
    }

    private static void AssertPair(IReadOnlyList<string> args, string name, string value)
    {
        var index = args.ToList().IndexOf(name);
        Assert.True(index >= 0 && index + 1 < args.Count, $"missing {name}");
        Assert.Equal(value, args[index + 1]);
    }

    private sealed class InlineProgress(Action<double> handler) : IProgress<double>
    {
        public void Report(double value) => handler(value);
    }
}
