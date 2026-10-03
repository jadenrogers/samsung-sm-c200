using Gear360.Core;
using Gear360.Mtp.LibMtp.Native;

namespace Gear360.Mtp.LibMtp;

/// <summary>Finds cameras over MTP using libmtp (macOS and Linux).</summary>
/// <remarks>
/// On Windows this always returns no cameras and never loads native code. On macOS and Linux it
/// throws <see cref="LibMtpNotFoundException"/> (with install instructions) when libmtp is missing.
/// </remarks>
public sealed class LibMtpCameraDiscovery : ICameraDiscovery
{
    /// <summary>Upper bound on raw devices read from libmtp's array; protects against a bad count.</summary>
    private const int MaxRawDevices = 128;

    /// <inheritdoc />
    /// <exception cref="LibMtpNotFoundException">libmtp is not installed (macOS and Linux only).</exception>
    /// <exception cref="LibMtpException">USB detection failed, or a Samsung device was found but could not be opened.</exception>
    public async Task<IReadOnlyList<ICameraSource>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return [];
        }

        LibMtpLibrary.EnsureLoaded();
        var thread = LibMtpThread.Instance;
        var devices = await thread.InvokeAsync(OpenCameras, cancellationToken).ConfigureAwait(false);
        return devices.Select(d => (ICameraSource)new LibMtpCameraSource(d, thread)).ToList();
    }

    /// <summary>The error shown when a Samsung device is attached but libmtp cannot open it.</summary>
    internal static string OpenFailedMessage(bool isMacOS) => isMacOS
        ? "A Samsung USB device was found but could not be opened. macOS usually has it claimed already: "
          + "quit Photos, Image Capture and Android File Transfer, run `killall ptpcamerad` in Terminal, then try again "
          + "(ptpcamerad restarts by itself, so run the command right before gear360). "
          + "Or use --source with the camera's microSD card in a card reader."
        : "A Samsung USB device was found but could not be opened. Close any file manager or app that has the camera open "
          + "(e.g. a desktop file manager mounting it through gvfs/kio), check that your user may access USB devices "
          + "(udev rules for libmtp), then try again. Or use --source with the camera's microSD card in a card reader.";

    /// <summary>Runs on the libmtp thread: detects raw devices and opens the ones that look like a Gear 360.</summary>
    private static unsafe List<LibMtpDevice> OpenCameras()
    {
        RawDevice* rawDevices = null;
        var count = 0;
        var status = LibMtpNative.DetectRawDevices(&rawDevices, &count);

        var nameMatches = new List<LibMtpDevice>();
        var fallbackMatches = new List<LibMtpDevice>();
        var openFailures = 0;
        try
        {
            if (status == MtpErrorNumber.NoDeviceAttached)
            {
                return [];
            }

            if (status != MtpErrorNumber.None)
            {
                throw new LibMtpException($"Could not scan USB for MTP devices (libmtp error {status}).");
            }

            if (rawDevices == null)
            {
                return [];
            }

            for (var i = 0; i < Math.Clamp(count, 0, MaxRawDevices); i++)
            {
                var raw = rawDevices[i];
                if (!CameraMatcher.IsCandidate(raw.DeviceEntry.VendorId))
                {
                    continue;
                }

                var device = LibMtpDevice.TryOpen(raw);
                if (device is null)
                {
                    openFailures++;
                    continue;
                }

                try
                {
                    if (CameraMatcher.NameMatches(device.FriendlyName, device.ModelName))
                    {
                        nameMatches.Add(device);
                    }
                    else if (CameraMatcher.IsMatch(device.VendorId, device.FriendlyName, device.ModelName, device.HasDcim))
                    {
                        fallbackMatches.Add(device);
                    }
                    else
                    {
                        device.Release();
                    }
                }
                catch (Exception ex)
                {
                    device.Release();

                    // A device that cannot even list its storage is not usable; skip it. Anything else is a bug.
                    if (ex is not LibMtpException)
                    {
                        throw;
                    }
                }
            }

            if (nameMatches.Count == 0 && fallbackMatches.Count == 0 && openFailures > 0)
            {
                throw new LibMtpException(OpenFailedMessage(OperatingSystem.IsMacOS()));
            }

            // Devices that call themselves a Gear 360 come first, so they are the default choice.
            return [.. nameMatches, .. fallbackMatches];
        }
        catch
        {
            foreach (var device in nameMatches.Concat(fallbackMatches))
            {
                device.Release();
            }

            throw;
        }
        finally
        {
            if (rawDevices != null)
            {
                // The array is malloc()ed by libmtp; the devices opened above use their own copies.
                System.Runtime.InteropServices.NativeMemory.Free(rawDevices);
            }
        }
    }
}
