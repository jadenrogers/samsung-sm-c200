namespace Gear360.Core;

/// <summary>A connected camera, or a folder that holds a camera's files.</summary>
public interface ICameraSource : IAsyncDisposable
{
    /// <summary>Human-readable name shown to the user, e.g. "Gear 360".</summary>
    string DisplayName { get; }

    /// <summary>Enumerates the photos and videos on the source.</summary>
    IAsyncEnumerable<CameraFile> EnumerateMediaAsync(CancellationToken cancellationToken = default);

    /// <summary>Copies a file's contents into <paramref name="destination"/>.</summary>
    /// <param name="file">A file returned by <see cref="EnumerateMediaAsync"/>.</param>
    /// <param name="destination">Writable stream that receives the bytes.</param>
    /// <param name="bytesProgress">Receives the running total of bytes copied.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    Task CopyToAsync(CameraFile file, Stream destination, IProgress<long>? bytesProgress = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file from the source.</summary>
    Task DeleteAsync(CameraFile file, CancellationToken cancellationToken = default);
}
