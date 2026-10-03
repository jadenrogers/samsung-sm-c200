namespace Gear360.Mtp.Windows;

/// <summary>Why talking to a portable device failed.</summary>
public enum WpdErrorKind
{
    /// <summary>Any other failure.</summary>
    Other,

    /// <summary>Another program (File Explorer, Photos, an AutoPlay import) is using the device.</summary>
    Busy,

    /// <summary>The device was unplugged, switched off or stopped responding.</summary>
    Disconnected,

    /// <summary>The device refused access, for example because it is locked or waiting for a prompt.</summary>
    AccessDenied,

    /// <summary>The file is no longer on the device.</summary>
    NotFound,

    /// <summary>Windows Portable Devices is not available on this PC.</summary>
    Unavailable,
}

/// <summary>A failure talking to a camera over MTP, with a message meant for the user.</summary>
public sealed class WpdDeviceException : IOException
{
    /// <summary>Creates the exception.</summary>
    public WpdDeviceException(WpdErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>Why the operation failed.</summary>
    public WpdErrorKind Kind { get; }
}
