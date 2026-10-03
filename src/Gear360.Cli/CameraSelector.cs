using System.CommandLine;
using Gear360.Core;
using Gear360.Platform;

namespace Gear360.Cli;

/// <summary>Options shared by commands that read from a camera, and the logic that turns them into a source.</summary>
internal static class CameraSelector
{
    public static Option<DirectoryInfo?> CreateSourceOption() => new("--source", "-s")
    {
        Description = "Read from a folder (e.g. a microSD card in a card reader) instead of a USB-connected camera.",
        HelpName = "folder",
    };

    public static Option<string?> CreateDeviceOption() => new("--device", "-d")
    {
        Description = "When several cameras are connected, use the one whose name contains this text.",
        HelpName = "name",
    };

    /// <summary>
    /// Returns the folder source when <paramref name="sourceFolder"/> is given, otherwise a camera found by
    /// platform discovery. Returns null (after printing why) when nothing usable is found.
    /// </summary>
    public static async Task<ICameraSource?> SelectAsync(DirectoryInfo? sourceFolder, string? device, CancellationToken cancellationToken)
    {
        if (sourceFolder is not null)
        {
            if (!sourceFolder.Exists)
            {
                Console.Error.WriteLine($"Folder not found: {sourceFolder.FullName}");
                return null;
            }

            return new FolderCameraSource(sourceFolder.FullName);
        }

        IReadOnlyList<ICameraSource> cameras;
        try
        {
            cameras = await PlatformCameraDiscovery.Create().DiscoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DllNotFoundException or IOException)
        {
            // Backends report missing native libraries and devices that cannot be opened with a user-facing message.
            Console.Error.WriteLine(ex.Message);
            return null;
        }

        if (cameras.Count == 0)
        {
            Console.Error.WriteLine("No Gear 360 camera was found.");
            Console.Error.WriteLine("  - Is the camera switched on?");
            Console.Error.WriteLine("  - Is the USB cable plugged in (and a data cable, not charge-only)?");
            Console.Error.WriteLine("  - Or take out the microSD card, put it in a card reader and use --source <folder>.");
            return null;
        }

        ICameraSource? chosen;
        if (!string.IsNullOrWhiteSpace(device))
        {
            chosen = cameras.FirstOrDefault(c => c.DisplayName.Contains(device, StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
            {
                Console.Error.WriteLine($"No connected camera matches '{device}'. Found: {string.Join(", ", cameras.Select(c => c.DisplayName))}");
            }
        }
        else
        {
            chosen = cameras[0];
            if (cameras.Count > 1)
            {
                Console.Error.WriteLine(
                    $"Found {cameras.Count} cameras ({string.Join(", ", cameras.Select(c => c.DisplayName))}); using {chosen.DisplayName}. Pick another with --device.");
            }
        }

        foreach (var camera in cameras.Where(c => !ReferenceEquals(c, chosen)))
        {
            await camera.DisposeAsync().ConfigureAwait(false);
        }

        return chosen;
    }
}
