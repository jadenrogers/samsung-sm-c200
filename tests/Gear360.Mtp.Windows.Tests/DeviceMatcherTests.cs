namespace Gear360.Mtp.Windows.Tests;

public sealed class DeviceMatcherTests
{
    private const string SamsungPnp = @"\\?\usb#vid_04e8&pid_6860&ms_comp_mtp&samsung#7&25fbdc9f&0&0000#{6ac27878-a6fa-4155-ba85-f98f491d4f33}";

    internal static readonly WpdDeviceInfo Gear360 = new("Gear 360", "Gear 360", "Samsung Electronics Co., Ltd.", "SM-C200", SamsungPnp);
    internal static readonly WpdDeviceInfo SamsungPhone = new("Galaxy S23", "Galaxy S23", "SAMSUNG", "SM-S911B", SamsungPnp, WpdDeviceKind.Phone);

    [Theory]
    [InlineData("Gear 360", null, null)]
    [InlineData("gear 360", null, null)]
    [InlineData(null, "SAMSUNG GEAR360", null)]
    [InlineData("MTP device", null, "SM-C200")]
    [InlineData(null, null, "sm-c200")]
    public void Name_description_or_model_mentioning_the_camera_is_recognised(string? friendly, string? description, string? model)
    {
        var info = new WpdDeviceInfo(friendly, description, null, model, null);

        Assert.Equal(DeviceMatch.Recognized, DeviceMatcher.Classify(info, hasDcim: false));
    }

    [Fact]
    public void Samsung_phone_with_DCIM_is_only_a_fallback()
    {
        Assert.Equal(DeviceMatch.Fallback, DeviceMatcher.Classify(SamsungPhone, hasDcim: true));
    }

    [Fact]
    public void Samsung_device_of_unknown_type_with_DCIM_is_only_a_fallback()
    {
        var info = SamsungPhone with { Kind = WpdDeviceKind.Unknown };

        Assert.Equal(DeviceMatch.Fallback, DeviceMatcher.Classify(info, hasDcim: true));
    }

    [Fact]
    public void Samsung_camera_with_DCIM_is_recognised()
    {
        var info = new WpdDeviceInfo("Camera", null, "Samsung Electronics", "XYZ", null, WpdDeviceKind.Camera);

        Assert.Equal(DeviceMatch.Recognized, DeviceMatcher.Classify(info, hasDcim: true));
    }

    [Fact]
    public void Samsung_vendor_id_counts_as_Samsung()
    {
        var info = new WpdDeviceInfo("Camera", null, null, null, SamsungPnp, WpdDeviceKind.Camera);

        Assert.True(DeviceMatcher.IsSamsung(info));
        Assert.Equal(DeviceMatch.Recognized, DeviceMatcher.Classify(info, hasDcim: true));
    }

    [Fact]
    public void Other_camera_with_DCIM_is_a_fallback()
    {
        var info = new WpdDeviceInfo("EOS 80D", null, "Canon Inc.", "Canon EOS 80D", null, WpdDeviceKind.Camera);

        Assert.Equal(DeviceMatch.Fallback, DeviceMatcher.Classify(info, hasDcim: true));
    }

    [Fact]
    public void Device_without_DCIM_is_ignored()
    {
        Assert.Equal(DeviceMatch.None, DeviceMatcher.Classify(SamsungPhone, hasDcim: false));
    }

    [Fact]
    public void Display_name_adds_the_model()
    {
        Assert.Equal("Gear 360 (SM-C200)", DeviceMatcher.GetDisplayName(Gear360, DeviceMatch.Recognized));
    }

    [Fact]
    public void Display_name_does_not_repeat_a_model_already_in_the_name()
    {
        var info = new WpdDeviceInfo("SM-C200", null, null, "SM-C200", null);

        Assert.Equal("SM-C200", DeviceMatcher.GetDisplayName(info, DeviceMatch.Recognized));
    }

    [Fact]
    public void Display_name_falls_back_to_description_then_a_generic_name()
    {
        Assert.Equal("Desc", DeviceMatcher.GetDisplayName(new WpdDeviceInfo(" ", "Desc", null, null, null), DeviceMatch.Recognized));
        Assert.Equal("MTP device", DeviceMatcher.GetDisplayName(new WpdDeviceInfo(null, null, null, null, null), DeviceMatch.Recognized));
    }

    [Fact]
    public void Fallback_display_name_is_flagged()
    {
        Assert.Equal(
            "Galaxy S23 (SM-S911B) - not recognised as a Gear 360",
            DeviceMatcher.GetDisplayName(SamsungPhone, DeviceMatch.Fallback));
    }
}
