using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using MediaDevices;

namespace Gear360.Mtp.Windows;

/// <summary>Turns COM and MediaDevices failures into <see cref="WpdDeviceException"/>s with friendly messages.</summary>
internal static partial class WpdErrors
{
    // Win32 errors surfaced as HRESULTs (0x8007xxxx) by the WPD stack.
    private const int ErrorFileNotFound = unchecked((int)0x80070002);
    private const int ErrorPathNotFound = unchecked((int)0x80070003);
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int ErrorNotReady = unchecked((int)0x80070015);
    private const int ErrorGenFailure = unchecked((int)0x8007001F);
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorDevNotExist = unchecked((int)0x80070037);
    private const int ErrorSemTimeout = unchecked((int)0x80070079);
    private const int ErrorBusy = unchecked((int)0x800700AA);
    private const int ErrorPipeBusy = unchecked((int)0x800700E7);
    private const int ErrorNoSuchDevice = unchecked((int)0x800701B1);
    private const int ErrorOperationAborted = unchecked((int)0x800703E3);
    private const int ErrorIoDevice = unchecked((int)0x8007045D);
    private const int ErrorDeviceNotConnected = unchecked((int)0x8007048F);

    /// <summary>Default HRESULTs of exception types, which say nothing about the device.</summary>
    private static readonly HashSet<int> GenericHResults =
    [
        unchecked((int)0x80131500), // COR_E_EXCEPTION
        unchecked((int)0x80131620), // COR_E_IO
        unchecked((int)0x80004005), // E_FAIL
    ];

    /// <summary>True for exceptions that come from the device stack and should be translated.</summary>
    public static bool ShouldTranslate(Exception exception) =>
        exception is not (WpdDeviceException or OperationCanceledException or ObjectDisposedException or ArgumentException) &&
        exception is MediaDeviceException or NotConnectedException or COMException or IOException or UnauthorizedAccessException
            or InvalidOperationException or ThreadSafeWorkerException;

    /// <summary>Works out why an operation failed by looking at HRESULTs and messages in the exception chain.</summary>
    public static WpdErrorKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is WpdDeviceException wpd)
            {
                return wpd.Kind;
            }

            if (current is NotConnectedException)
            {
                return WpdErrorKind.Disconnected;
            }

            if (current is FileNotFoundException or DirectoryNotFoundException)
            {
                return WpdErrorKind.NotFound;
            }

            foreach (var hresult in HResultsOf(current))
            {
                var kind = FromHResult(hresult);
                if (kind != WpdErrorKind.Other)
                {
                    return kind;
                }
            }

            var message = current.Message;
            if (message.Contains("in use", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("busy", StringComparison.OrdinalIgnoreCase))
            {
                return WpdErrorKind.Busy;
            }

            if (message.Contains("not functioning", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("not connected", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("disconnected", StringComparison.OrdinalIgnoreCase))
            {
                return WpdErrorKind.Disconnected;
            }

            if (message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            {
                return WpdErrorKind.AccessDenied;
            }
        }

        return WpdErrorKind.Other;
    }

    /// <summary>Maps an HRESULT to an error kind.</summary>
    public static WpdErrorKind FromHResult(int hresult) => hresult switch
    {
        ErrorBusy or ErrorSharingViolation or ErrorPipeBusy => WpdErrorKind.Busy,
        ErrorDeviceNotConnected or ErrorGenFailure or ErrorNoSuchDevice or ErrorDevNotExist or ErrorNotReady
            or ErrorSemTimeout or ErrorOperationAborted or ErrorIoDevice => WpdErrorKind.Disconnected,
        AccessDenied => WpdErrorKind.AccessDenied,
        ErrorFileNotFound or ErrorPathNotFound => WpdErrorKind.NotFound,
        _ => WpdErrorKind.Other,
    };

    /// <summary>Wraps <paramref name="exception"/> in a <see cref="WpdDeviceException"/> with a message for the user.</summary>
    /// <param name="exception">The original failure; kept as the inner exception.</param>
    /// <param name="deviceName">The device's display name.</param>
    /// <param name="operation">What was being done, phrased to follow "while trying to", e.g. "copy SAM_0001.MP4".</param>
    public static WpdDeviceException Translate(Exception exception, string deviceName, string operation)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is WpdDeviceException existing)
        {
            return existing;
        }

        var kind = Classify(exception);
        var message = kind switch
        {
            WpdErrorKind.Busy =>
                $"{deviceName} is busy: another program is using it (for example File Explorer, the Photos app or an AutoPlay import). " +
                "Close that program and try again.",
            WpdErrorKind.Disconnected =>
                $"Lost the connection to {deviceName} while trying to {operation}. " +
                "Check that the camera is still switched on and plugged in, then try again. Files already copied are kept.",
            WpdErrorKind.AccessDenied =>
                $"{deviceName} refused access while trying to {operation}. " +
                "If the device is locked or shows a USB prompt, unlock it or accept the prompt, then try again.",
            WpdErrorKind.NotFound =>
                $"Could not {operation}: it is no longer on {deviceName}. List the files again.",
            _ => $"Could not {operation} on {deviceName}: {exception.Message}",
        };
        return new WpdDeviceException(kind, message, exception);
    }

    private static IEnumerable<int> HResultsOf(Exception exception)
    {
        if (exception.HResult < 0 && !GenericHResults.Contains(exception.HResult))
        {
            yield return exception.HResult;
        }

        foreach (Match match in HexCode().Matches(exception.Message))
        {
            if (uint.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                yield return unchecked((int)value);
            }
        }
    }

    [GeneratedRegex(@"0x([0-9A-Fa-f]{8})\b")]
    private static partial Regex HexCode();
}
