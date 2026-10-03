using Gear360.Core;
using Gear360.Mtp.LibMtp;
#if WINDOWS
using Gear360.Mtp.Windows;
#endif

namespace Gear360.Platform;

/// <summary>Chooses the camera discovery backend for the current operating system.</summary>
public static class PlatformCameraDiscovery
{
    /// <summary>
    /// Returns the discovery for this OS: Windows Portable Devices on Windows (in the
    /// <c>net10.0-windows</c> build) and libmtp elsewhere.
    /// </summary>
    public static ICameraDiscovery Create()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return new CompositeCameraDiscovery([new WpdCameraDiscovery()]);
        }
#endif
        if (OperatingSystem.IsWindows())
        {
            // The portable net10.0 build has no WPD backend, and libmtp is not used on Windows.
            return new CompositeCameraDiscovery([]);
        }

        return new CompositeCameraDiscovery([new LibMtpCameraDiscovery()]);
    }
}
