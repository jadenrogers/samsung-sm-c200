using System.Runtime.CompilerServices;

namespace Gear360.Core;

/// <summary>
/// A camera source backed by a folder, such as a mounted microSD card. When the folder contains a
/// <c>DCIM</c> subfolder only that subfolder is searched; otherwise the whole folder is searched.
/// </summary>
public sealed class FolderCameraSource : ICameraSource
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>Creates a source for <paramref name="rootPath"/>.</summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    public FolderCameraSource(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(RootPath))
        {
            throw new DirectoryNotFoundException($"Folder not found: {RootPath}");
        }

        DisplayName = $"Folder {RootPath}";
    }

    /// <summary>The absolute path of the folder.</summary>
    public string RootPath { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<CameraFile> EnumerateMediaAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var searchRoot = FindSearchRoot();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var files = new DirectoryInfo(searchRoot)
            .EnumerateFiles("*", options)
            .Where(f => MediaKinds.FromPath(f.Name) != MediaKind.Other)
            .Select(ToCameraFile)
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CopyToAsync(CameraFile file, Stream destination, IProgress<long>? bytesProgress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var path = ResolvePath(file);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            bytesProgress?.Report(total);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(CameraFile file, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(ResolvePath(file));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string FindSearchRoot()
    {
        var dcim = Directory.EnumerateDirectories(RootPath)
            .FirstOrDefault(d => Path.GetFileName(d).Equals("DCIM", StringComparison.OrdinalIgnoreCase));
        return dcim ?? RootPath;
    }

    private CameraFile ToCameraFile(FileInfo info)
    {
        var relative = Path.GetRelativePath(RootPath, info.FullName).Replace(Path.DirectorySeparatorChar, '/');
        return new CameraFile(relative, relative, info.Length, new DateTimeOffset(info.LastWriteTime));
    }

    /// <summary>Maps a file back to an absolute path, refusing anything outside the root folder.</summary>
    private string ResolvePath(CameraFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var full = Path.GetFullPath(Path.Combine(RootPath, file.RelativePath));
        var root = Path.EndsInDirectorySeparator(RootPath) ? RootPath : RootPath + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{file.RelativePath}' is outside {RootPath}.", nameof(file));
        }

        return full;
    }
}
