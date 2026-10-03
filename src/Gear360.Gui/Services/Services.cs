using Gear360.Core.Stitching;

namespace Gear360.Gui.Services;

/// <summary>Runs work on the UI thread.</summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> to run on the UI thread.</summary>
    void Post(Action action);
}

/// <summary>Dialogs and shell actions the view model needs from the window.</summary>
public interface IDialogService
{
    /// <summary>Asks the user for a folder; returns null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startFolder);

    /// <summary>Asks the user for a file; returns null when cancelled.</summary>
    Task<string?> PickFileAsync(string title);

    /// <summary>Asks a yes/no question; returns true for yes.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>Opens a folder in the system file manager.</summary>
    Task OpenFolderAsync(string path);
}

/// <summary>Finds ffmpeg and stitches files. Wraps <see cref="FfmpegLocator"/> and <see cref="Stitcher"/> so tests can replace them.</summary>
public interface IStitchService
{
    /// <summary>Finds ffmpeg and ffprobe, or returns null when they are missing.</summary>
    FfmpegTools? TryLocate(string? explicitPath);

    /// <summary>The encoder stitching will use (Auto resolved to a working hardware encoder or the CPU).</summary>
    /// <exception cref="StitchException">An explicitly chosen hardware encoder does not work on this computer.</exception>
    Task<StitchEncoder> ResolveEncoderAsync(FfmpegTools tools, StitchOptions options, CancellationToken cancellationToken);

    /// <summary>Stitches one file.</summary>
    Task<StitchResult> StitchAsync(FfmpegTools tools, string inputPath, StitchOptions options, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>The ffmpeg build that can be downloaded for this machine, or null when there is none.</summary>
    FfmpegPackage? DownloadPackage { get; }

    /// <summary>Downloads and installs <see cref="DownloadPackage"/>; only ever called after the user asked for it.</summary>
    /// <exception cref="FfmpegInstallException">The download or install failed.</exception>
    Task<FfmpegTools> InstallFfmpegAsync(IProgress<(long Done, long? Total)>? progress, CancellationToken cancellationToken);
}

/// <summary>The real ffmpeg-based <see cref="IStitchService"/>.</summary>
public sealed class FfmpegStitchService : IStitchService
{
    /// <inheritdoc />
    public FfmpegTools? TryLocate(string? explicitPath) => FfmpegLocator.TryLocate(explicitPath);

    /// <inheritdoc />
    public Task<StitchEncoder> ResolveEncoderAsync(FfmpegTools tools, StitchOptions options, CancellationToken cancellationToken) =>
        new Stitcher(tools).ResolveEncoderAsync(options, cancellationToken);

    /// <inheritdoc />
    public Task<StitchResult> StitchAsync(FfmpegTools tools, string inputPath, StitchOptions options, IProgress<double>? progress, CancellationToken cancellationToken) =>
        new Stitcher(tools).StitchAsync(inputPath, options, progress, cancellationToken);

    /// <inheritdoc />
    public FfmpegPackage? DownloadPackage => FfmpegManifest.ForCurrentPlatform();

    /// <inheritdoc />
    public Task<FfmpegTools> InstallFfmpegAsync(IProgress<(long Done, long? Total)>? progress, CancellationToken cancellationToken) =>
        new FfmpegInstaller().InstallAsync(progress, cancellationToken);
}
