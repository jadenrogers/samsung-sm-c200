namespace Gear360.Mtp.LibMtp.Tests;

public sealed class CameraMatcherTests
{
    [Theory]
    [InlineData("Gear 360", null)]
    [InlineData(null, "SM-C200")]
    [InlineData("My gear360", null)]
    [InlineData("", "Samsung SM-C200 camera")]
    public void Names_that_identify_a_Gear_360_match(string? friendly, string? model)
    {
        Assert.True(CameraMatcher.IsMatch(0x1234, friendly, model, () => throw new InvalidOperationException("not needed")));
    }

    [Fact]
    public void Samsung_device_with_DCIM_matches_as_fallback()
    {
        Assert.True(CameraMatcher.IsMatch(CameraMatcher.SamsungVendorId, null, "SM-G991B", () => true));
    }

    [Fact]
    public void Samsung_device_without_DCIM_does_not_match()
    {
        Assert.False(CameraMatcher.IsMatch(CameraMatcher.SamsungVendorId, "Phone", "SM-G991B", () => false));
    }

    [Fact]
    public void Other_vendor_with_DCIM_does_not_match()
    {
        Assert.False(CameraMatcher.IsMatch(0x18D1, "Pixel", "Pixel 8", () => true));
    }

    [Fact]
    public void Only_Samsung_devices_are_opened()
    {
        Assert.True(CameraMatcher.IsCandidate(0x04E8));
        Assert.False(CameraMatcher.IsCandidate(0x18D1));
    }

    [Theory]
    [InlineData("Gear 360", "SM-C200", null, "Gear 360 (SM-C200)")]
    [InlineData("Gear 360 SM-C200", "SM-C200", null, "Gear 360 SM-C200")]
    [InlineData(null, "SM-C200", null, "SM-C200")]
    [InlineData("  ", null, "Samsung", "Samsung")]
    [InlineData(null, null, null, "MTP device")]
    public void Display_name_combines_the_names_it_has(string? friendly, string? model, string? product, string expected)
    {
        Assert.Equal(expected, CameraMatcher.DisplayName(friendly, model, product));
    }
}
