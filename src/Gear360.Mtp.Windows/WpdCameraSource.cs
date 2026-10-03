using System.Runtime.CompilerServices;

using Gear360.Core;

namespace Gear360.Mtp.Windows;

/// <summary>
/// A camera connected over MTP, read through Windows Portable Devices. Every device call runs on a
/// dedicated STA worker thread, so the members can be called from any thread.
/// </summary>
public sealed class WpdCameraSource : ICameraSource
{
    /// <summary>Bytes requested from the device per read.</summary>
    internal const int ChunkSize = 1024 * 1024;

    private readonly IWpdDevice _device;
    private readonly StaTaskScheduler _scheduler;
    private readonly string _deviceName;
    private int _disposed;

    internal WpdCameraSource(IWpdDevice device, WpdDeviceInfo info, DeviceMatch match, StaTaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(scheduler);
        _device = device;
        _scheduler = scheduler;
        IsRecognizedCamera = match == DeviceMatch.Recognized;
        DisplayName = DeviceMatcher.GetDisplayName(info, match);
        _deviceName = DeviceMatcher.GetDisplayName(info, DeviceMatch.Recognized);
    }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <summary>
    /// True when the device was identified as a Gear 360. False for a fallback device (any MTP device
    /// with a DCIM folder, such as a phone), which can be listed and copied from but never deleted from.
    /// </summary>
    public bool IsRecognizedCamera { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<CameraFile> EnumerateMediaAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var entries = await RunAsync(_device.ListMedia, "list its files", cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new CameraFile(entry.Id, entry.RelativePath, entry.Size, ToOffset(entry.Modified));
        }
    }

    /// <inheritdoc />
    public async Task CopyToAsync(CameraFile file, Stream destination, IProgress<long>? bytesProgress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);
        var operation = $"copy {file.Name}";

        var input = await RunAsync(() => _device.OpenRead(file.Id), operation, cancellationToken).ConfigureAwait(false);
        try
        {
            var buffer = new byte[ChunkSize];
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await RunAsync(() => input.Read(buffer, 0, buffer.Length), operation, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
                bytesProgress?.Report(total);
            }
        }
        finally
        {
            await CloseQuietlyAsync(input).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The device was not recognised as a Gear 360.</exception>
    public Task DeleteAsync(CameraFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!IsRecognizedCamera)
        {
            throw new InvalidOperationException(
                $"Not deleting {file.Name}: {_deviceName} was not recognised as a Gear 360, and files are only deleted from a recognised camera.");
        }

        return RunAsync(() => _device.Delete(file.Id), $"delete {file.Name}", cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _scheduler.RunAsync(_device.Dispose).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The worker has already stopped (process shutdown); the device goes away with it.
        }
    }

    private static DateTimeOffset? ToOffset(DateTime? value)
    {
        if (value is not { } time || time == default)
        {
            return null;
        }

        // WPD reports device-local times without a zone; treat them as this PC's local time.
        return time.Kind == DateTimeKind.Utc ? new DateTimeOffset(time) : new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Local));
    }

    private Task RunAsync(Action action, string operation, CancellationToken cancellationToken) =>
        RunAsync(
            () =>
            {
                action();
                return true;
            },
            operation,
            cancellationToken);

    private async Task<T> RunAsync<T>(Func<T> function, string operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            return await _scheduler.RunAsync(function, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (WpdErrors.ShouldTranslate(ex))
        {
            throw WpdErrors.Translate(ex, DisplayName, operation);
        }
    }

    private async Task CloseQuietlyAsync(Stream stream)
    {
        try
        {
            await _scheduler.RunAsync(stream.Dispose).ConfigureAwait(false);
        }
        catch (Exception ex) when (WpdErrors.ShouldTranslate(ex) || ex is ObjectDisposedException)
        {
            // Closing a stream on a device that has gone away fails too; the real error was already reported.
        }
    }
}
