using System.Runtime.InteropServices;

using MediaDevices;

namespace Gear360.Mtp.Windows.Tests;

public sealed class WpdPathsAndErrorsTests
{
    [Theory]
    [InlineData(@"\Card\", @"\Card\DCIM\100PHOTO\SAM_0001.MP4", "DCIM/100PHOTO/SAM_0001.MP4")]
    [InlineData(@"\Card", @"\Card\DCIM\100PHOTO\SAM_0001.MP4", "DCIM/100PHOTO/SAM_0001.MP4")]
    [InlineData(@"\card\", @"\Card\DCIM\SAM_0002.JPG", "DCIM/SAM_0002.JPG")]
    [InlineData(@"\", @"\DCIM\SAM_0003.JPG", "DCIM/SAM_0003.JPG")]
    [InlineData(@"\Other\", @"\Card\DCIM\100PHOTO\SAM_0004.MP4", "DCIM/100PHOTO/SAM_0004.MP4")]
    [InlineData(@"\Other\", @"\Card\Misc\SAM_0005.MP4", "SAM_0005.MP4")]
    public void Relative_path_is_from_the_storage_root_with_forward_slashes(string root, string file, string expected)
    {
        Assert.Equal(expected, WpdPaths.ToRelativePath(root, file));
    }

    [Theory]
    [InlineData(0x800700AA, WpdErrorKind.Busy)]
    [InlineData(0x80070020, WpdErrorKind.Busy)]
    [InlineData(0x8007048F, WpdErrorKind.Disconnected)]
    [InlineData(0x8007001F, WpdErrorKind.Disconnected)]
    [InlineData(0x800701B1, WpdErrorKind.Disconnected)]
    [InlineData(0x80070079, WpdErrorKind.Disconnected)]
    [InlineData(0x80070005, WpdErrorKind.AccessDenied)]
    [InlineData(0x80070002, WpdErrorKind.NotFound)]
    [InlineData(0x80004005, WpdErrorKind.Other)]
    public void COM_HRESULTs_are_classified(uint hresult, WpdErrorKind expected)
    {
        Assert.Equal(expected, WpdErrors.Classify(new COMException("failed", unchecked((int)hresult))));
    }

    [Fact]
    public void HRESULT_in_a_MediaDevices_message_is_classified()
    {
        var ex = new MediaDeviceException("Error 0x800700AA in IPortableDevice.Open");

        Assert.Equal(WpdErrorKind.Busy, WpdErrors.Classify(ex));
    }

    [Fact]
    public void Inner_exceptions_are_searched()
    {
        var ex = new InvalidOperationException("outer", new COMException("inner", unchecked((int)0x8007048F)));

        Assert.Equal(WpdErrorKind.Disconnected, WpdErrors.Classify(ex));
    }

    [Fact]
    public void NotConnectedException_means_disconnected()
    {
        Assert.Equal(WpdErrorKind.Disconnected, WpdErrors.Classify(new NotConnectedException("Not connected")));
    }

    [Fact]
    public void MediaDevices_not_functioning_message_means_disconnected()
    {
        var ex = new MediaDeviceException("A mediaDevice attached to the system is not functioning.");

        Assert.Equal(WpdErrorKind.Disconnected, WpdErrors.Classify(ex));
    }

    [Fact]
    public void Busy_message_names_the_device_and_the_likely_culprits()
    {
        var ex = WpdErrors.Translate(new COMException("in use", unchecked((int)0x800700AA)), "Gear 360 (SM-C200)", "open it");

        Assert.Equal(WpdErrorKind.Busy, ex.Kind);
        Assert.Contains("Gear 360 (SM-C200) is busy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("File Explorer", ex.Message, StringComparison.Ordinal);
        Assert.IsType<COMException>(ex.InnerException);
    }

    [Fact]
    public void Disconnect_message_says_what_was_happening()
    {
        var ex = WpdErrors.Translate(new NotConnectedException("gone"), "Gear 360", "copy SAM_0001.MP4");

        Assert.Equal(WpdErrorKind.Disconnected, ex.Kind);
        Assert.Contains("while trying to copy SAM_0001.MP4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_failures_keep_the_original_message()
    {
        var ex = WpdErrors.Translate(new IOException("strange"), "Gear 360", "list its files");

        Assert.Equal(WpdErrorKind.Other, ex.Kind);
        Assert.Equal("Could not list its files on Gear 360: strange", ex.Message);
    }

    [Fact]
    public void Cancellation_and_argument_errors_are_not_translated()
    {
        Assert.False(WpdErrors.ShouldTranslate(new OperationCanceledException()));
        Assert.False(WpdErrors.ShouldTranslate(new ArgumentException("x")));
        Assert.False(WpdErrors.ShouldTranslate(new WpdDeviceException(WpdErrorKind.Busy, "x")));
        Assert.True(WpdErrors.ShouldTranslate(new COMException("x")));
    }
}
