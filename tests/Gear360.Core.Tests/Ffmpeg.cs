using System.Diagnostics;
using System.Globalization;
using Gear360.Core.Stitching;

namespace Gear360.Core.Tests;

/// <summary>Runs ffmpeg/ffprobe for tests that need real media. Tests skip themselves when ffmpeg is not installed.</summary>
internal static class Ffmpeg
{
    private static readonly Lazy<FfmpegTools?> LazyTools = new(() => FfmpegLocator.TryLocate());

    public static FfmpegTools? Tools => LazyTools.Value;

    /// <summary>Skips the calling test when ffmpeg is missing; otherwise returns the tools.</summary>
    public static FfmpegTools Require()
    {
        Skip.If(Tools is null, "ffmpeg/ffprobe not found");
        return Tools!;
    }

    /// <summary>Writes a test-pattern H.264 clip with an AAC audio track.</summary>
    public static void CreateClip(string path, int width, int height, double seconds, bool faststart)
    {
        var args = new List<string>
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"testsrc2=s={width}x{height}:d={seconds}:r=25"),
            "-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"sine=frequency=440:duration={seconds}"),
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-shortest",
        };
        if (faststart)
        {
            args.AddRange(["-movflags", "+faststart"]);
        }

        args.Add(path);
        var result = Run(Require().FfmpegPath, args);
        Assert.True(result.ExitCode == 0, result.Error);
    }

    /// <summary>Writes a single test-pattern JPEG.</summary>
    public static void CreatePhoto(string path, int width, int height)
    {
        var result = Run(Require().FfmpegPath, ["-y", "-v", "error", "-f", "lavfi", "-i", $"testsrc2=s={width}x{height}", "-frames:v", "1", "-update", "1", path]);
        Assert.True(result.ExitCode == 0, result.Error);
    }

    public static (int Width, int Height, double Duration) Probe(string path)
    {
        var result = Run(Require().FfprobePath, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:format=duration", "-of", "json", path]);
        Assert.True(result.ExitCode == 0, result.Error);
        var info = Stitcher.ParseProbeOutput(result.Output);
        return (info.Width, info.Height, info.Duration?.TotalSeconds ?? 0);
    }

    /// <summary>ffprobe's JSON description of the first video stream, including side data such as spherical mapping.</summary>
    public static string ProbeVideoStream(string path)
    {
        var result = Run(Require().FfprobePath, ["-v", "error", "-select_streams", "v:0", "-show_streams", "-of", "json", path]);
        Assert.True(result.ExitCode == 0, result.Error);
        return result.Output;
    }

    /// <summary>Decodes every stream and returns per-frame checksums; fails the test if ffmpeg reports any error.</summary>
    public static string DecodeChecksums(string path)
    {
        var result = Run(Require().FfmpegPath, ["-v", "error", "-i", path, "-f", "framemd5", "-"]);
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.True(string.IsNullOrWhiteSpace(result.Error), "ffmpeg reported decode errors: " + result.Error);

        // Drop the comment header, which names the input file.
        return string.Join('\n', result.Output.Split('\n').Where(l => !l.StartsWith('#')));
    }

    public static (int ExitCode, string Output, string Error) Run(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.Result);
    }
}
