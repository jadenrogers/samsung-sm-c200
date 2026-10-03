namespace Gear360.Core.Import;

/// <summary>State of one file during an import.</summary>
public enum ImportFileStatus
{
    /// <summary>Bytes are being copied; reported repeatedly as the copy advances.</summary>
    Copying,

    /// <summary>The file was copied and verified.</summary>
    Copied,

    /// <summary>The destination already held the same file, so nothing was copied.</summary>
    Skipped,

    /// <summary>The file could not be copied (or could not be deleted afterwards).</summary>
    Failed,
}

/// <summary>A progress report for one file of an import.</summary>
/// <param name="File">The file being imported.</param>
/// <param name="Index">1-based position of the file in the import.</param>
/// <param name="Total">Number of files in the import.</param>
/// <param name="BytesCopied">Bytes copied so far for this file.</param>
/// <param name="FileSize">Size of the file in bytes.</param>
/// <param name="Status">Current state of the file.</param>
/// <param name="OutputPath">Destination path, once known.</param>
/// <param name="Error">Error message when <paramref name="Status"/> is <see cref="ImportFileStatus.Failed"/>.</param>
public sealed record ImportProgress(
    CameraFile File,
    int Index,
    int Total,
    long BytesCopied,
    long FileSize,
    ImportFileStatus Status,
    string? OutputPath = null,
    string? Error = null);
