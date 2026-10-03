using Gear360.Core;

namespace Gear360.Mtp.Windows;

/// <summary>
/// Finds cameras over MTP using Windows Portable Devices (WPD). A device is recognised as a Gear 360
/// when its name, description or model contains "Gear 360" or "SM-C200", or when it is a Samsung
/// camera with a DCIM folder. When no Gear 360 is connected, other MTP devices with a DCIM folder are
/// returned as fallbacks, flagged in their <see cref="ICameraSource.DisplayName"/>.
/// </summary>
public sealed class WpdCameraDiscovery : ICameraDiscovery
{
    private readonly Func<IReadOnlyList<IWpdDevice>> _enumerateDevices;
    private readonly StaTaskScheduler _scheduler;

    /// <summary>Creates a discovery that uses the Windows Portable Devices API.</summary>
    public WpdCameraDiscovery()
        : this(MediaDevicesWpdDevice.GetAll, StaTaskScheduler.Shared)
    {
    }

    internal WpdCameraDiscovery(Func<IReadOnlyList<IWpdDevice>> enumerateDevices, StaTaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(enumerateDevices);
        ArgumentNullException.ThrowIfNull(scheduler);
        _enumerateDevices = enumerateDevices;
        _scheduler = scheduler;
    }

    /// <summary>
    /// When true (the default) and no Gear 360 is connected, other MTP devices with a DCIM folder
    /// (phones, other cameras) are returned, flagged as not recognised. Files are never deleted from them.
    /// </summary>
    public bool IncludeUnrecognizedDevices { get; init; } = true;

    /// <inheritdoc />
    /// <exception cref="WpdDeviceException">
    /// A Gear 360 is connected but could not be opened (for example because another program is using it),
    /// or Windows Portable Devices is not available.
    /// </exception>
    public async Task<IReadOnlyList<ICameraSource>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var probes = await _scheduler.RunAsync(() => ProbeAll(cancellationToken), cancellationToken).ConfigureAwait(false);

        var recognized = probes.Where(p => p.Match == DeviceMatch.Recognized).ToList();
        var chosen = recognized.Count > 0 || !IncludeUnrecognizedDevices
            ? recognized
            : probes.Where(p => p.Match == DeviceMatch.Fallback).ToList();

        var unused = probes.Except(chosen).Select(p => p.Device).ToList();
        if (unused.Count > 0)
        {
            await _scheduler.RunAsync(() => unused.ForEach(DisposeQuietly), CancellationToken.None).ConfigureAwait(false);
        }

        if (recognized.Count == 0 && probes.FirstOrDefault(p => p.Failure is not null) is { } failed)
        {
            // A Gear 360 is plugged in but could not be opened: say why instead of "no camera found".
            foreach (var probe in chosen)
            {
                await _scheduler.RunAsync(() => DisposeQuietly(probe.Device), CancellationToken.None).ConfigureAwait(false);
            }

            throw failed.Failure!;
        }

        return chosen.Select(p => (ICameraSource)new WpdCameraSource(p.Device, p.Info, p.Match, _scheduler)).ToList();
    }

    /// <summary>Connects to each device and classifies it. Runs on the worker thread.</summary>
    private List<Probe> ProbeAll(CancellationToken cancellationToken)
    {
        IReadOnlyList<IWpdDevice> devices;
        try
        {
            devices = _enumerateDevices();
        }
        catch (Exception ex) when (WpdErrors.ShouldTranslate(ex) || ex is TypeInitializationException or DllNotFoundException
                                       or EntryPointNotFoundException)
        {
            throw new WpdDeviceException(
                WpdErrorKind.Unavailable,
                "Windows Portable Devices (MTP support) is not available on this PC. On Windows N editions, install the " +
                "Media Feature Pack; or copy the files from the camera's microSD card with a card reader and use --source.",
                ex);
        }

        var probes = new List<Probe>();
        foreach (var device in devices)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                DisposeQuietly(device);
                continue;
            }

            probes.Add(ProbeOne(device));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            probes.ForEach(p => DisposeQuietly(p.Device));
            cancellationToken.ThrowIfCancellationRequested();
        }

        return probes;
    }

    private static Probe ProbeOne(IWpdDevice device)
    {
        var info = device.GetInfo();
        try
        {
            device.Connect();
            info = device.GetInfo();
            var match = DeviceMatcher.Classify(info, device.HasDcim());
            return new Probe(device, info, match, null);
        }
        catch (Exception ex) when (WpdErrors.ShouldTranslate(ex))
        {
            // Only a failure on something that looks like the camera is worth reporting; a locked phone is not.
            var failure = DeviceMatcher.NamesGear360(info)
                ? WpdErrors.Translate(ex, DeviceMatcher.GetDisplayName(info, DeviceMatch.Recognized), "open it")
                : null;
            return new Probe(device, info, DeviceMatch.None, failure);
        }
    }

    private static void DisposeQuietly(IWpdDevice device)
    {
        try
        {
            device.Dispose();
        }
        catch (Exception ex) when (WpdErrors.ShouldTranslate(ex))
        {
            // Closing a device that is unusable anyway; nothing to report.
        }
    }

    private sealed record Probe(IWpdDevice Device, WpdDeviceInfo Info, DeviceMatch Match, WpdDeviceException? Failure);
}
