using System.Globalization;
using System.Runtime.CompilerServices;

using Gear360.Core;
using Gear360.Mtp.LibMtp.Native;

namespace Gear360.Mtp.LibMtp;

/// <summary>A camera connected over USB and read through libmtp.</summary>
internal sealed class LibMtpCameraSource : ICameraSource
{
    private readonly LibMtpDevice _device;
    private readonly LibMtpThread _thread;
    private int _disposed;

    public LibMtpCameraSource(LibMtpDevice device, LibMtpThread thread)
    {
        _device = device;
        _thread = thread;
        DisplayName = CameraMatcher.DisplayName(device.FriendlyName, device.ModelName, device.ManufacturerName);
    }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<CameraFile> EnumerateMediaAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // The listing has to finish on the libmtp thread anyway, so collect it there and yield afterwards.
        var files = await _thread.InvokeAsync(() => _device.FindMedia(cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    /// <inheritdoc />
    public Task CopyToAsync(CameraFile file, Stream destination, IProgress<long>? bytesProgress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var itemId = ParseId(file);
        ThrowIfDisposed();

        // The destination is written synchronously from libmtp's callback on the libmtp thread.
        return _thread.InvokeAsync(() => _device.CopyTo(itemId, destination, bytesProgress, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(CameraFile file, CancellationToken cancellationToken = default)
    {
        var itemId = ParseId(file);
        ThrowIfDisposed();
        return _thread.InvokeAsync(() => _device.Delete(itemId), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _thread.InvokeAsync(_device.Release).ConfigureAwait(false);
        }
    }

    private static uint ParseId(CameraFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return uint.TryParse(file.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new ArgumentException($"'{file.Id}' is not an MTP object id; the file did not come from this camera.", nameof(file));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
