namespace Gear360.Mtp.Windows;

/// <summary>The broad type a portable device reports for itself.</summary>
internal enum WpdDeviceKind
{
    /// <summary>The device did not say, or said something else.</summary>
    Unknown,

    /// <summary>A camera.</summary>
    Camera,

    /// <summary>A mobile phone.</summary>
    Phone,
}

/// <summary>Identifying details of a portable device. Any field may be missing.</summary>
/// <param name="FriendlyName">User-visible name, e.g. "Gear 360".</param>
/// <param name="Description">Short description from the driver.</param>
/// <param name="Manufacturer">Manufacturer name, e.g. "Samsung Electronics Co., Ltd.".</param>
/// <param name="Model">Model name, e.g. "SM-C200".</param>
/// <param name="PnpDeviceId">Plug and Play device path, which contains the USB vendor id.</param>
/// <param name="Kind">The device type the device reports.</param>
internal sealed record WpdDeviceInfo(
    string? FriendlyName,
    string? Description,
    string? Manufacturer,
    string? Model,
    string? PnpDeviceId,
    WpdDeviceKind Kind = WpdDeviceKind.Unknown);
