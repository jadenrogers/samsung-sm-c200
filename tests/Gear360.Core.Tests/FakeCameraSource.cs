using System.Runtime.CompilerServices;

namespace Gear360.Core.Tests;

/// <summary>An in-memory camera whose copies can be made to fail, stall or cancel part way.</summary>
internal sealed class FakeCameraSource : ICameraSource
{
    private readonly Dictionary<string, byte[]> _contents = new(StringComparer.Ordinal);
    private readonly List<CameraFile> _files = [];

    public string DisplayName => "Fake camera";

    /// <summary>Paths whose copy throws an <see cref="IOException"/> after writing half the bytes.</summary>
    public HashSet<string> FailingPaths { get; } = new(StringComparer.Ordinal);

    /// <summary>Paths whose copy writes fewer bytes than the reported size, without throwing.</summary>
    public HashSet<string> ShortPaths { get; } = new(StringComparer.Ordinal);

    /// <summary>Paths whose delete throws.</summary>
    public HashSet<string> UndeletablePaths { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, this token source is cancelled once half of the named file has been written.</summary>
    public (string Path, CancellationTokenSource Source)? CancelDuring { get; set; }

    public List<string> Deleted { get; } = [];

    public CameraFile Add(string relativePath, int size, DateTimeOffset? modified)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        _contents[relativePath] = bytes;
        var file = new CameraFile("id:" + relativePath, relativePath, size, modified);
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
        var half = bytes.Length / 2;
        await destination.WriteAsync(bytes.AsMemory(0, half), cancellationToken);
        bytesProgress?.Report(half);

        if (FailingPaths.Contains(file.RelativePath))
        {
            throw new IOException("Simulated USB disconnect.");
        }

        if (CancelDuring is { } cancel && cancel.Path == file.RelativePath)
        {
            await cancel.Source.CancelAsync();
        }

        cancellationToken.ThrowIfCancellationRequested();

        var end = ShortPaths.Contains(file.RelativePath) ? bytes.Length - 1 : bytes.Length;
        await destination.WriteAsync(bytes.AsMemory(half, end - half), cancellationToken);
        bytesProgress?.Report(end);
    }

    public Task DeleteAsync(CameraFile file, CancellationToken cancellationToken = default)
    {
        if (UndeletablePaths.Contains(file.RelativePath))
        {
            throw new IOException("Simulated delete failure.");
        }

        _files.RemoveAll(f => f.RelativePath == file.RelativePath);
        Deleted.Add(file.RelativePath);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public byte[] ContentOf(string relativePath) => _contents[relativePath];
}
