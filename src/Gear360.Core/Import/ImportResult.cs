namespace Gear360.Core.Import;

/// <summary>A file that failed to import.</summary>
/// <param name="File">The source file.</param>
/// <param name="Message">What went wrong.</param>
public sealed record ImportFailure(CameraFile File, string Message);

/// <summary>The outcome of an import.</summary>
/// <param name="Total">Number of files the import considered (after filtering).</param>
/// <param name="CopiedPaths">Destination paths of the files copied by this run.</param>
/// <param name="SkippedPaths">Destination paths of files that were already present.</param>
/// <param name="Failures">Files that failed.</param>
/// <param name="DeletedCount">Number of files deleted from the source after copying.</param>
public sealed record ImportResult(
    int Total,
    IReadOnlyList<string> CopiedPaths,
    IReadOnlyList<string> SkippedPaths,
    IReadOnlyList<ImportFailure> Failures,
    int DeletedCount)
{
    /// <summary>True when no file failed.</summary>
    public bool Succeeded => Failures.Count == 0;
}
