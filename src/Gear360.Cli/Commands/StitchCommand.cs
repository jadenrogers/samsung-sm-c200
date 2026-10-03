using System.CommandLine;
using Gear360.Core;
using Gear360.Core.Stitching;

namespace Gear360.Cli.Commands;

/// <summary><c>gear360 stitch</c>: converts dual-fisheye files into equirectangular 360 files with ffmpeg.</summary>
internal static class StitchCommand
{
    public static Command Create()
    {
        var inputs = new Argument<string[]>("inputs")
        {
            Description = "Videos or photos to stitch, or folders (every video in the folder and its subfolders is stitched).",
            Arity = ArgumentArity.OneOrMore,
        };
        var output = new Option<DirectoryInfo?>("--output", "-o")
        {
            Description = "Folder for the stitched files (default: next to each input).",
            HelpName = "dir",
        };
        var overwrite = new Option<bool>("--overwrite") { Description = "Replace stitched files that already exist." };
        var stitchOptions = new StitchCliOptions();

        var command = new Command(
            "stitch",
            "Stitch dual-fisheye Gear 360 videos and photos into 360 (equirectangular) files. Needs ffmpeg.")
        {
            inputs, output, overwrite,
        };
        foreach (var option in stitchOptions.All)
        {
            command.Options.Add(option);
        }

        command.SetAction((parseResult, cancellationToken) =>
        {
            var (profile, options) = stitchOptions.Read(parseResult, parseResult.GetValue(output)?.FullName, parseResult.GetValue(overwrite));
            return RunAsync(parseResult.GetValue(inputs)!, parseResult.GetValue(stitchOptions.Ffmpeg), parseResult.GetValue(stitchOptions.GetFfmpeg), profile, options, cancellationToken);
        });
        return command;
    }

    private static async Task<int> RunAsync(string[] inputs, string? ffmpegPath, bool getFfmpeg, StitchProfile profile, StitchOptions options, CancellationToken cancellationToken)
    {
        var files = ExpandInputs(inputs, out var missing);
        if (missing.Count > 0)
        {
            foreach (var path in missing)
            {
                Console.Error.WriteLine($"Not found: {path}");
            }

            return ExitCodes.NoCamera;
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("Nothing to stitch: no videos found in the given folders.");
            return ExitCodes.NoCamera;
        }

        var (tools, exitCode) = await StitchRunner.LocateToolsAsync(ffmpegPath, getFfmpeg, cancellationToken);
        if (tools is null)
        {
            return exitCode;
        }

        try
        {
            if (await StitchRunner.ResolveEncoderAsync(tools, options, profile, cancellationToken) is not { } encoder)
            {
                return ExitCodes.ToolMissing;
            }

            var failed = await StitchRunner.RunAsync(tools, files, options, encoder, cancellationToken);
            return failed == 0 ? ExitCodes.Success : ExitCodes.Failed;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled. The partly stitched file was removed.");
            return ExitCodes.Cancelled;
        }
    }

    /// <summary>
    /// Files are taken as given; folders contribute the videos in them and their subfolders, skipping files this tool
    /// already produced. Duplicates are removed.
    /// </summary>
    internal static IReadOnlyList<string> ExpandInputs(IEnumerable<string> inputs, out List<string> missing)
    {
        missing = [];
        var files = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var full = Path.GetFullPath(input);
            if (File.Exists(full))
            {
                if (seen.Add(full))
                {
                    files.Add(full);
                }
            }
            else if (Directory.Exists(full))
            {
                var videos = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                    .Where(f => MediaKinds.FromPath(f) == MediaKind.Video && !Stitcher.IsStitchedOutput(f))
                    .Order(StringComparer.OrdinalIgnoreCase);
                foreach (var video in videos)
                {
                    if (seen.Add(video))
                    {
                        files.Add(video);
                    }
                }
            }
            else
            {
                missing.Add(input);
            }
        }

        return files;
    }
}
