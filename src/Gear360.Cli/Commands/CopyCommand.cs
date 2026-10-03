using System.CommandLine;
using Gear360.Core;
using Gear360.Core.Import;
using Gear360.Core.Stitching;

namespace Gear360.Cli.Commands;

/// <summary><c>gear360 copy</c>: copies media into dated folders under an output directory.</summary>
internal static class CopyCommand
{
    public static Command Create()
    {
        var output = new Option<DirectoryInfo>("--output", "-o")
        {
            Description = "Folder to copy into. Files go to <output>/<yyyy-MM-dd>/<name>.",
            HelpName = "dir",
            Required = true,
        };
        var source = CameraSelector.CreateSourceOption();
        var device = CameraSelector.CreateDeviceOption();
        var videosOnly = new Option<bool>("--videos-only") { Description = "Copy videos only, not photos." };
        var since = new Option<DateTime?>("--since")
        {
            Description = "Copy only files from this date on (e.g. 2024-06-01).",
            HelpName = "date",
        };
        var deleteAfter = new Option<bool>("--delete-after")
        {
            Description = "Delete each file from the camera after it has been copied and verified.",
        };

        var stitch = new Option<bool>("--stitch")
        {
            Description = "After copying, also stitch each video into a 360 file (<name>_stitched.mp4) next to it. Needs ffmpeg.",
        };
        var stitchOptions = new StitchCliOptions();

        var command = new Command("copy", "Copy photos and videos from the camera to a folder.")
        {
            output, source, device, videosOnly, since, deleteAfter, stitch,
        };
        foreach (var option in stitchOptions.All)
        {
            command.Options.Add(option);
        }

        command.SetAction((parseResult, cancellationToken) =>
        {
            var sinceDate = parseResult.GetValue(since);
            var options = new ImportOptions
            {
                Destination = parseResult.GetValue(output)!.FullName,
                VideosOnly = parseResult.GetValue(videosOnly),
                Since = sinceDate is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d.Date, DateTimeKind.Local)) : null,
                DeleteAfter = parseResult.GetValue(deleteAfter),
            };
            StitchSettings? stitchSettings = null;
            if (parseResult.GetValue(stitch))
            {
                var (profile, stitchOpts) = stitchOptions.Read(parseResult, outputDirectory: null, overwrite: false);
                stitchSettings = new StitchSettings(parseResult.GetValue(stitchOptions.Ffmpeg), parseResult.GetValue(stitchOptions.GetFfmpeg), profile, stitchOpts);
            }

            return RunAsync(parseResult.GetValue(source), parseResult.GetValue(device), options, stitchSettings, cancellationToken);
        });
        return command;
    }

    private static async Task<int> RunAsync(
        DirectoryInfo? sourceFolder,
        string? device,
        ImportOptions options,
        StitchSettings? stitch,
        CancellationToken cancellationToken)
    {
        // Look for ffmpeg before copying anything, so a missing install is reported straight away.
        FfmpegTools? tools = null;
        var encoder = StitchEncoder.Cpu;
        if (stitch is not null)
        {
            int exitCode;
            (tools, exitCode) = await StitchRunner.LocateToolsAsync(stitch.FfmpegPath, stitch.GetFfmpeg, cancellationToken);
            if (tools is null)
            {
                return exitCode;
            }

            try
            {
                if (await StitchRunner.ResolveEncoderAsync(tools, stitch.Options, stitch.Profile, cancellationToken) is not { } resolved)
                {
                    return ExitCodes.ToolMissing;
                }

                encoder = resolved;
            }
            catch (OperationCanceledException)
            {
                return ExitCodes.Cancelled;
            }
        }

        var progress = new ConsoleImportProgress();
        try
        {
            await using var camera = await CameraSelector.SelectAsync(sourceFolder, device, cancellationToken);
            if (camera is null)
            {
                return ExitCodes.NoCamera;
            }

            Console.WriteLine($"Copying from {camera.DisplayName} to {options.Destination}");
            var result = await new ImportService().ImportAsync(camera, options, progress, cancellationToken);

            Console.WriteLine(
                $"Done: {result.CopiedPaths.Count} copied, {result.SkippedPaths.Count} skipped, {result.Failures.Count} failed" +
                (options.DeleteAfter ? $", {result.DeletedCount} deleted from the camera." : "."));

            var stitchFailures = 0;
            if (stitch is not null && tools is not null)
            {
                // Files skipped because they were already copied are included, so an interrupted stitch can be
                // resumed; videos whose _stitched file already exists are skipped by the stitcher.
                var videos = result.CopiedPaths.Concat(result.SkippedPaths)
                    .Where(p => MediaKinds.FromPath(p) == MediaKind.Video && !Stitcher.IsStitchedOutput(p))
                    .ToList();
                if (videos.Count > 0)
                {
                    stitchFailures = await StitchRunner.RunAsync(tools, videos, stitch.Options, encoder, cancellationToken);
                }
                else
                {
                    Console.WriteLine("No videos to stitch.");
                }
            }

            return result.Succeeded && stitchFailures == 0 ? ExitCodes.Success : ExitCodes.Failed;
        }
        catch (OperationCanceledException)
        {
            progress.Interrupt();
            Console.Error.WriteLine("Cancelled. Files already finished were kept; the partial file was removed.");
            return ExitCodes.Cancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            progress.Interrupt();
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failed;
        }
    }

    /// <summary>What <c>--stitch</c> asked for.</summary>
    private sealed record StitchSettings(string? FfmpegPath, bool GetFfmpeg, StitchProfile Profile, StitchOptions Options);
}
