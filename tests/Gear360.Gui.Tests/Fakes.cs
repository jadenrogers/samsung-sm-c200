using System.Runtime.CompilerServices;
using Gear360.Core;
using Gear360.Core.Stitching;
using Gear360.Gui.Services;

namespace Gear360.Gui.Tests;

/// <summary>An in-memory camera that records whether it was disposed.</summary>
internal sealed class FakeCameraSource(string name = "Fake camera") : ICameraSource
{
    private readonly Dictionary<string, byte[]> _contents = new(StringComparer.Ordinal);
    private readonly List<CameraFile> _files = [];

    public string DisplayName => name;

    public bool IsDisposed { get; private set; }

    public List<string> Deleted { get; } = [];

    public CameraFile Add(string relativePath, int size, DateTimeOffset? modified = null)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        _contents[relativePath] = bytes;
        var file = new CameraFile("id:" + relativePath, relativePath, size, modified ?? new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero));
        _files.Add(file);
        return file;
    }

    public async IAsyncEnumerable<CameraFile> EnumerateMediaAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var file in _files.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return file;
        }
    }

    public async Task CopyToAsync(CameraFile file, Stream destination, IProgress<long>? bytesProgress = null, CancellationToken cancellationToken = default)
    {
        var bytes = _contents[file.RelativePath];
        await destination.WriteAsync(bytes, cancellationToken);
        bytesProgress?.Report(bytes.Length);
    }

    public Task DeleteAsync(CameraFile file, CancellationToken cancellationToken = default)
    {
        _files.RemoveAll(f => f.RelativePath == file.RelativePath);
        Deleted.Add(file.RelativePath);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Returns a fixed list of new cameras on each call, or throws.</summary>
internal sealed class FakeDiscovery : ICameraDiscovery
{
    public Func<IReadOnlyList<ICameraSource>> Next { get; set; } = () => [];

    public List<ICameraSource> Returned { get; } = [];

    public Task<IReadOnlyList<ICameraSource>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var cameras = Next();
        Returned.AddRange(cameras);
        return Task.FromResult(cameras);
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public string? FolderToPick { get; set; }

    public bool ConfirmAnswer { get; set; } = true;

    public int ConfirmCount { get; private set; }

    public Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult(FolderToPick);

    public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        ConfirmCount++;
        return Task.FromResult(ConfirmAnswer);
    }

    public Task OpenFolderAsync(string path) => Task.CompletedTask;
}

/// <summary>Pretends to stitch by writing a small <c>_stitched.mp4</c> file.</summary>
internal sealed class FakeStitchService : IStitchService
{
    public bool Found { get; set; } = true;

    public List<string> Stitched { get; } = [];

    public StitchOptions? LastOptions { get; private set; }

    public FfmpegTools? TryLocate(string? explicitPath) => Found ? new FfmpegTools("ffmpeg", "ffprobe") : null;

    /// <summary>The package offered for download; null (no download offered) unless a test sets it.</summary>
    public FfmpegPackage? DownloadPackage { get; set; }

    public int InstallCount { get; private set; }

    /// <summary>When set, the install throws this instead of succeeding.</summary>
    public Exception? InstallFailure { get; set; }

    public Task<FfmpegTools> InstallFfmpegAsync(IProgress<(long Done, long? Total)>? progress, CancellationToken cancellationToken)
    {
        InstallCount++;
        if (InstallFailure is not null)
        {
            return Task.FromException<FfmpegTools>(InstallFailure);
        }

        progress?.Report((50, 100));
        progress?.Report((100, 100));
        Found = true;
        return Task.FromResult(new FfmpegTools(System.IO.Path.Combine("downloaded", "ffmpeg"), System.IO.Path.Combine("downloaded", "ffprobe")));
    }

    public static FfmpegPackage TestPackage { get; } = new(
        "test-x64",
        "1.0",
        "Test publisher",
        "https://example.invalid/",
        "GNU GPL v3",
        "https://example.invalid/source",
        [new("https://example.invalid/ffmpeg.zip", new string('0', 64), 100 * 1024 * 1024, FfmpegArchiveKind.Zip, new Dictionary<string, string> { ["ffmpeg"] = "ffmpeg" })]);

    /// <summary>The encoder Auto resolves to.</summary>
    public StitchEncoder AutoEncoder { get; set; } = StitchEncoder.Nvidia;

    /// <summary>Explicit encoders that "do not work" on this fake machine.</summary>
    public HashSet<StitchEncoder> Unusable { get; } = [];

    public Task<StitchEncoder> ResolveEncoderAsync(FfmpegTools tools, StitchOptions options, CancellationToken cancellationToken) =>
        options.Encoder == StitchEncoder.Auto ? Task.FromResult(AutoEncoder)
        : Unusable.Contains(options.Encoder) ? Task.FromException<StitchEncoder>(new StitchException($"The {options.Encoder.DisplayName()} encoder cannot be used."))
        : Task.FromResult(options.Encoder);

    public async Task<StitchResult> StitchAsync(FfmpegTools tools, string inputPath, StitchOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Stitched.Add(inputPath);
        LastOptions = options;
        progress?.Report(0.5);
        var output = Stitcher.GetOutputPath(inputPath, options);
        File.WriteAllText(output, "stitched");
        progress?.Report(1);
        return new StitchResult(inputPath, output, Skipped: false, await ResolveEncoderAsync(tools, options, cancellationToken));
    }
}

internal sealed class MemorySettingsStore : ISettingsStore
{
    public AppSettings Current { get; set; } = new();

    public int SaveCount { get; private set; }

    public AppSettings Load() => Current;

    public void Save(AppSettings settings)
    {
        Current = settings;
        SaveCount++;
    }
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    private readonly Lock _lock = new();

    public void Post(Action action)
    {
        lock (_lock)
        {
            action();
        }
    }
}

/// <summary>A unique temporary folder that is deleted when disposed.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gear360-gui-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public IReadOnlyList<string> ListFiles() =>
        Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(Path, f).Replace(System.IO.Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
