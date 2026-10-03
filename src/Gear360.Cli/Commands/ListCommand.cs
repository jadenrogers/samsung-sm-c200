using System.CommandLine;
using System.Globalization;
using Gear360.Core;

namespace Gear360.Cli.Commands;

/// <summary><c>gear360 list</c>: prints the photos and videos on the camera.</summary>
internal static class ListCommand
{
    public static Command Create()
    {
        var source = CameraSelector.CreateSourceOption();
        var device = CameraSelector.CreateDeviceOption();

        var command = new Command("list", "List the photos and videos on the camera.") { source, device };
        command.SetAction((parseResult, cancellationToken) =>
            RunAsync(parseResult.GetValue(source), parseResult.GetValue(device), cancellationToken));
        return command;
    }

    private static async Task<int> RunAsync(DirectoryInfo? sourceFolder, string? device, CancellationToken cancellationToken)
    {
        try
        {
            await using var camera = await CameraSelector.SelectAsync(sourceFolder, device, cancellationToken);
            if (camera is null)
            {
                return ExitCodes.NoCamera;
            }

            var files = new List<CameraFile>();
            await foreach (var file in camera.EnumerateMediaAsync(cancellationToken))
            {
                files.Add(file);
            }

            Console.WriteLine($"{camera.DisplayName}: {files.Count} file(s)");
            if (files.Count == 0)
            {
                return ExitCodes.Success;
            }

            var pathWidth = Math.Max("Path".Length, files.Max(f => f.RelativePath.Length));
            Console.WriteLine($"{"Path".PadRight(pathWidth)}  {"Kind",-5}  {"Size",10}  Date");
            foreach (var file in files)
            {
                var date = file.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";
                Console.WriteLine($"{file.RelativePath.PadRight(pathWidth)}  {file.Kind,-5}  {ByteSize.Format(file.Size),10}  {date}");
            }

            Console.WriteLine($"Total: {ByteSize.Format(files.Sum(f => f.Size))}");
            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failed;
        }
    }
}
