namespace Gear360.Core.Stitching;

/// <summary>Paths to the ffmpeg and ffprobe executables.</summary>
/// <param name="FfmpegPath">Full path to ffmpeg.</param>
/// <param name="FfprobePath">Full path to ffprobe.</param>
public sealed record FfmpegTools(string FfmpegPath, string FfprobePath);

/// <summary>ffmpeg or ffprobe could not be found.</summary>
public sealed class FfmpegNotFoundException : Exception
{
    /// <summary>Creates the exception with a message that includes install instructions.</summary>
    public FfmpegNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Finds ffmpeg and ffprobe: an explicit path first, then the folders on <c>PATH</c>, then common install folders,
/// then a copy downloaded by <see cref="FfmpegInstaller"/>.
/// </summary>
/// <remarks>
/// The downloaded copy comes last so that an ffmpeg the user installed and keeps up to date (winget, Homebrew, ...)
/// always wins; the download is only offered when nothing else was found.
/// </remarks>
public static class FfmpegLocator
{
    /// <summary>How to install ffmpeg on the current OS.</summary>
    public static string InstallHint =>
        OperatingSystem.IsWindows() ? "Install it with 'winget install Gyan.FFmpeg' (or from https://ffmpeg.org/download.html), then open a new terminal."
        : OperatingSystem.IsMacOS() ? "Install it with 'brew install ffmpeg'."
        : "Install it with your package manager, e.g. 'sudo apt install ffmpeg'.";

    /// <summary>Finds ffmpeg and ffprobe.</summary>
    /// <param name="explicitPath">
    /// The ffmpeg executable or the folder that holds it (e.g. from <c>--ffmpeg</c>); null to search.
    /// ffprobe is looked for next to it first.
    /// </param>
    /// <exception cref="FfmpegNotFoundException">Either tool is missing.</exception>
    public static FfmpegTools Locate(string? explicitPath = null) => Locate(explicitPath, SearchFolders());

    /// <summary>Like <see cref="Locate(string?)"/>, searching <paramref name="searchFolders"/> instead of the usual folders.</summary>
    internal static FfmpegTools Locate(string? explicitPath, IEnumerable<string> searchFolders)
    {
        var folders = searchFolders.ToList();
        string? explicitFolder = null;
        string? ffmpeg;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (Directory.Exists(full))
            {
                explicitFolder = full;
                ffmpeg = FindIn(full, "ffmpeg");
            }
            else if (File.Exists(full))
            {
                explicitFolder = Path.GetDirectoryName(full);
                ffmpeg = full;
            }
            else
            {
                ffmpeg = null;
            }

            if (ffmpeg is null)
            {
                throw new FfmpegNotFoundException($"ffmpeg was not found at '{explicitPath}'.");
            }
        }
        else
        {
            ffmpeg = Find("ffmpeg", null, folders);
            explicitFolder = ffmpeg is null ? null : Path.GetDirectoryName(ffmpeg);
            if (ffmpeg is null)
            {
                throw new FfmpegNotFoundException(
                    $"ffmpeg is needed to stitch 360 video but was not found. {InstallHint} Or pass its location with --ffmpeg <path>.");
            }
        }

        var ffprobe = Find("ffprobe", explicitFolder, folders)
            ?? throw new FfmpegNotFoundException(
                $"ffprobe (which comes with ffmpeg) was not found next to {ffmpeg} or on PATH. {InstallHint}");
        return new FfmpegTools(ffmpeg, ffprobe);
    }

    /// <summary>Like <see cref="Locate"/>, but returns null instead of throwing.</summary>
    public static FfmpegTools? TryLocate(string? explicitPath = null)
    {
        try
        {
            return Locate(explicitPath);
        }
        catch (FfmpegNotFoundException)
        {
            return null;
        }
    }

    private static string? Find(string tool, string? preferredFolder, IEnumerable<string> folders)
    {
        if (preferredFolder is not null && FindIn(preferredFolder, tool) is { } preferred)
        {
            return preferred;
        }

        foreach (var folder in folders)
        {
            if (FindIn(folder, tool) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    internal static IEnumerable<string> SearchFolders()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return folder.Trim('"');
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (localAppData.Length > 0)
            {
                yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
            }

            if (programFiles.Length > 0)
            {
                yield return Path.Combine(programFiles, "ffmpeg", "bin");
            }

            yield return @"C:\ffmpeg\bin";
            yield return @"C:\ProgramData\chocolatey\bin";
        }
        else
        {
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
            yield return "/usr/bin";
            yield return "/opt/local/bin";
        }

        foreach (var folder in FfmpegInstaller.InstalledFolders())
        {
            yield return folder;
        }
    }

    /// <summary>The first line of <c>ffmpeg -version</c>, or null when ffmpeg could not be run.</summary>
    public static async Task<string?> GetVersionAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ToolProcess.RunAsync(ffmpegPath, ["-hide_banner", "-version"], null, cancellationToken).ConfigureAwait(false);
            var first = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return result.ExitCode == 0 ? first : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="ffmpegPath"/> is a copy downloaded by <see cref="FfmpegInstaller"/>.</summary>
    public static bool IsDownloadedCopy(string ffmpegPath, string? dataDirectory = null)
    {
        var root = Path.GetFullPath(FfmpegInstaller.DefaultRoot(dataDirectory)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Path.GetFullPath(ffmpegPath).StartsWith(root, comparison);
    }

    private static string? FindIn(string folder, string tool)
    {
        try
        {
            var candidate = Path.Combine(folder, OperatingSystem.IsWindows() ? tool + ".exe" : tool);
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }
        catch (ArgumentException)
        {
            // A malformed PATH entry.
            return null;
        }
    }
}
