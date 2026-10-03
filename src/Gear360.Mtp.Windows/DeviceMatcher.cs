namespace Gear360.Mtp.Windows;

/// <summary>How confident discovery is that a portable device is a Gear 360.</summary>
internal enum DeviceMatch
{
    /// <summary>Not a candidate (no DCIM folder).</summary>
    None,

    /// <summary>Some other device with a DCIM folder, offered only when no Gear 360 is connected.</summary>
    Fallback,

    /// <summary>A Gear 360: its name or model says so, or it is a Samsung camera with a DCIM folder.</summary>
    Recognized,
}

/// <summary>Decides whether a portable device is a Gear 360 and how to name it.</summary>
internal static class DeviceMatcher
{
    /// <summary>Text that identifies the camera in a device's name, description or model.</summary>
    private static readonly string[] CameraNames = ["Gear 360", "Gear360", "SM-C200"];

    /// <summary>Suffix added to the name of a device that was not recognised as a Gear 360.</summary>
    public const string FallbackSuffix = " - not recognised as a Gear 360";

    /// <summary>True when the name, description or model mentions the Gear 360 (case-insensitive).</summary>
    public static bool NamesGear360(WpdDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        string?[] fields = [info.FriendlyName, info.Description, info.Model, info.Manufacturer];
        return fields.Any(field => field is not null &&
            CameraNames.Any(name => field.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>True when the manufacturer is Samsung or the USB vendor id is Samsung's (04E8).</summary>
    public static bool IsSamsung(WpdDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return (info.Manufacturer?.Contains("Samsung", StringComparison.OrdinalIgnoreCase) ?? false) ||
               (info.PnpDeviceId?.Contains("VID_04E8", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// Classifies a device. Samsung phones also expose a DCIM folder over the same USB ids as the
    /// camera, so a Samsung device without "Gear 360"/"SM-C200" in its name only counts as
    /// recognised when it reports itself as a camera; anything else is at most a fallback.
    /// </summary>
    public static DeviceMatch Classify(WpdDeviceInfo info, bool hasDcim)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (NamesGear360(info))
        {
            return DeviceMatch.Recognized;
        }

        if (!hasDcim)
        {
            return DeviceMatch.None;
        }

        return IsSamsung(info) && info.Kind == WpdDeviceKind.Camera ? DeviceMatch.Recognized : DeviceMatch.Fallback;
    }

    /// <summary>
    /// The name shown to the user, such as "Gear 360 (SM-C200)". Fallback devices are flagged so a
    /// phone is never mistaken for the camera.
    /// </summary>
    public static string GetDisplayName(WpdDeviceInfo info, DeviceMatch match)
    {
        ArgumentNullException.ThrowIfNull(info);
        var name = FirstNonEmpty(info.FriendlyName, info.Description, info.Model) ?? "MTP device";
        var model = info.Model?.Trim();
        if (!string.IsNullOrEmpty(model) && !name.Contains(model, StringComparison.OrdinalIgnoreCase))
        {
            name = $"{name} ({model})";
        }

        return match == DeviceMatch.Fallback ? name + FallbackSuffix : name;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
