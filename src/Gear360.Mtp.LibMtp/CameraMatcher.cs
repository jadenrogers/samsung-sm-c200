namespace Gear360.Mtp.LibMtp;

/// <summary>Decides whether an MTP device is a Gear 360 camera.</summary>
internal static class CameraMatcher
{
    /// <summary>Samsung's USB vendor id.</summary>
    public const ushort SamsungVendorId = 0x04E8;

    private static readonly string[] s_cameraNames = ["Gear 360", "Gear360", "SM-C200"];

    /// <summary>
    /// Whether a device is worth opening at all. Only Samsung devices are opened, so other phones and
    /// players plugged into the computer are left alone.
    /// </summary>
    public static bool IsCandidate(ushort vendorId) => vendorId == SamsungVendorId;

    /// <summary>Whether any of the device's names identifies it as a Gear 360.</summary>
    public static bool NameMatches(params ReadOnlySpan<string?> names)
    {
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            foreach (var cameraName in s_cameraNames)
            {
                if (name.Contains(cameraName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A device matches when its name says it is a Gear 360, or, as a fallback, when it is a Samsung
    /// device with a <c>DCIM</c> folder. <paramref name="hasDcim"/> is only called for the fallback.
    /// </summary>
    public static bool IsMatch(ushort vendorId, string? friendlyName, string? modelName, Func<bool> hasDcim)
    {
        ArgumentNullException.ThrowIfNull(hasDcim);
        return NameMatches(friendlyName, modelName) || (vendorId == SamsungVendorId && hasDcim());
    }

    /// <summary>The name shown to the user, e.g. "Gear 360 (SM-C200)".</summary>
    public static string DisplayName(string? friendlyName, string? modelName, string? productName)
    {
        var friendly = Clean(friendlyName);
        var model = Clean(modelName);
        if (friendly is not null && model is not null && !friendly.Contains(model, StringComparison.OrdinalIgnoreCase))
        {
            return $"{friendly} ({model})";
        }

        return friendly ?? model ?? Clean(productName) ?? "MTP device";
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
