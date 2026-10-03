using System.Globalization;

namespace Gear360.Core.Import;

/// <summary>
/// Copies media from an <see cref="ICameraSource"/> into a destination folder, one subfolder per day.
/// Files are written to <c>&lt;name&gt;.part</c> first and renamed once their size has been verified,
/// so an interrupted import never leaves a truncated file under its real name.
/// </summary>
public sealed class ImportService
{
    /// <summary>Folder name used for files that have no date.</summary>
    public const string UndatedFolder = "undated";

    /// <summary>Suffix of the temporary file written while copying.</summary>
    public const string PartialSuffix = ".part";

    private const int BufferSize = 1024 * 1024;

    /// <summary>Enumerates the source, applies the filters in <paramref name="options"/> and imports the matching files.</summary>
    /// <exception cref="OperationCanceledException">The import was cancelled; any partial file has been removed.</exception>
    public async Task<ImportResult> ImportAsync(
        ICameraSource source,
        ImportOptions options,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        var files = new List<CameraFile>();
        await foreach (var file in source.EnumerateMediaAsync(cancellationToken).ConfigureAwait(false))
        {
            if (options.Includes(file))
            {
                files.Add(file);
            }
        }

        return await ImportFilesAsync(source, files, options, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports exactly <paramref name="files"/> (for example a user's selection). The
    /// <see cref="ImportOptions.VideosOnly"/> and <see cref="ImportOptions.Since"/> filters are not applied.
    /// </summary>
    /// <exception cref="OperationCanceledException">The import was cancelled; any partial file has been removed.</exception>
    public async Task<ImportResult> ImportFilesAsync(
        ICameraSource source,
        IReadOnlyList<CameraFile> files,
        ImportOptions options,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Destination);

        var copied = new List<string>();
        var skipped = new List<string>();
        var failures = new List<ImportFailure>();
        var deleted = 0;

        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            var index = i + 1;

            string target;
            bool alreadyPresent;
            try
            {
                (target, alreadyPresent) = ChooseTarget(file, options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Fail(file, index, 0, null, ex.Message);
                continue;
            }

            if (alreadyPresent)
            {
                skipped.Add(target);
                Report(file, index, file.Size, ImportFileStatus.Skipped, target);
                continue;
            }

            Report(file, index, 0, ImportFileStatus.Copying, target);
            var bytesProgress = progress is null
                ? null
                : new SyncProgress<long>(bytes => Report(file, index, bytes, ImportFileStatus.Copying, target));

            try
            {
                await CopyOneAsync(source, file, target, options.Overwrite, bytesProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Fail(file, index, 0, target, ex.Message);
                continue;
            }

            copied.Add(target);

            if (options.DeleteAfter)
            {
                try
                {
                    if (new FileInfo(target).Length != file.Size)
                    {
                        throw new IOException("The copied file's size does not match, so the original was kept.");
                    }

                    await source.DeleteAsync(file, cancellationToken).ConfigureAwait(false);
                    deleted++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Fail(file, index, file.Size, target, $"Copied, but not deleted from the source: {ex.Message}");
                    continue;
                }
            }

            Report(file, index, file.Size, ImportFileStatus.Copied, target);
        }

        return new ImportResult(files.Count, copied, skipped, failures, deleted);

        void Report(CameraFile file, int index, long bytes, ImportFileStatus status, string? target, string? error = null) =>
            progress?.Report(new ImportProgress(file, index, files.Count, bytes, file.Size, status, target, error));

        void Fail(CameraFile file, int index, long bytes, string? target, string message)
        {
            failures.Add(new ImportFailure(file, message));
            Report(file, index, bytes, ImportFileStatus.Failed, target, message);
        }
    }

    /// <summary>Returns the day folder name for a file: <c>yyyy-MM-dd</c> in local time, or <see cref="UndatedFolder"/>.</summary>
    public static string GetDateFolder(CameraFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.Modified is { } modified
            ? modified.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : UndatedFolder;
    }

    /// <summary>
    /// Picks the destination path. A file of the same name and size counts as already imported.
    /// A same-named file of a different size is overwritten when <see cref="ImportOptions.Overwrite"/>
    /// is set; otherwise the new file gets a numbered name such as <c>SAM_0001 (1).MP4</c>.
    /// </summary>
    private static (string Target, bool AlreadyPresent) ChooseTarget(CameraFile file, ImportOptions options)
    {
        var name = Path.GetFileName(file.Name);
        if (string.IsNullOrEmpty(name) || name is "." or "..")
        {
            throw new ArgumentException($"Invalid file name '{file.RelativePath}'.");
        }

        var folder = Path.Combine(Path.GetFullPath(options.Destination), GetDateFolder(file));
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var n = 0; ; n++)
        {
            var candidate = Path.Combine(folder, n == 0 ? name : $"{stem} ({n}){extension}");
            var info = new FileInfo(candidate);
            if (!info.Exists)
            {
                return (candidate, false);
            }

            if (info.Length == file.Size)
            {
                return (candidate, true);
            }

            if (options.Overwrite)
            {
                return (candidate, false);
            }
        }
    }

    private static async Task CopyOneAsync(
        ICameraSource source,
        CameraFile file,
        string target,
        bool overwrite,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + PartialSuffix;
        try
        {
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await source.CopyToAsync(file, output, bytesProgress, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var written = new FileInfo(partial).Length;
            if (written != file.Size)
            {
                throw new IOException($"Copied {written} bytes but the source reported {file.Size}.");
            }

            File.Move(partial, target, overwrite);
            if (file.Modified is { } modified)
            {
                File.SetLastWriteTimeUtc(target, modified.UtcDateTime);
            }
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover .part file is harmless and is replaced by the next run.
        }
    }

    /// <summary>Calls the handler synchronously, unlike <see cref="Progress{T}"/> which posts to a context.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
