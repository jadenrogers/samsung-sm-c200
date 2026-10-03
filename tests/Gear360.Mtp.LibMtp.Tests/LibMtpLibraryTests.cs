using Gear360.Mtp.LibMtp.Native;

namespace Gear360.Mtp.LibMtp.Tests;

public sealed class LibMtpLibraryTests
{
    [Fact]
    public void TryLoad_on_Windows_reports_unavailable_without_loading_anything()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.False(LibMtpLibrary.TryLoad(out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Throws<LibMtpNotFoundException>(LibMtpLibrary.EnsureLoaded);
    }

    [Fact]
    public async Task Discovery_on_Windows_returns_no_cameras()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cameras = await new LibMtpCameraDiscovery().DiscoverAsync();
        Assert.Empty(cameras);
    }

    [Fact]
    public void TryLoad_failure_is_a_friendly_message_or_success()
    {
        // On any OS: either libmtp loads, or the error explains why in plain words (never an exception).
        var loaded = LibMtpLibrary.TryLoad(out var error);
        Assert.True(loaded || !string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void MacOS_candidates_probe_Homebrew_folders_first()
    {
        var candidates = LibMtpLibrary.GetCandidates(isMacOS: true, overridePath: null);

        Assert.Equal("/opt/homebrew/lib/libmtp.9.dylib", candidates[0]);
        Assert.Contains("/opt/homebrew/lib/libmtp.dylib", candidates);
        Assert.Contains("/usr/local/lib/libmtp.9.dylib", candidates);
        Assert.Contains("/usr/local/lib/libmtp.dylib", candidates);
        Assert.DoesNotContain(candidates, c => c.Contains(".so", StringComparison.Ordinal));
    }

    [Fact]
    public void Linux_candidates_use_the_soname()
    {
        var candidates = LibMtpLibrary.GetCandidates(isMacOS: false, overridePath: null);

        Assert.Equal("libmtp.so.9", candidates[0]);
        Assert.DoesNotContain(candidates, c => c.Contains(".dylib", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Override_path_is_tried_first(bool isMacOS)
    {
        var candidates = LibMtpLibrary.GetCandidates(isMacOS, "  /custom/libmtp.so  ");
        Assert.Equal("/custom/libmtp.so", candidates[0]);
    }

    [Fact]
    public void Not_found_message_on_macOS_says_how_to_install()
    {
        Assert.Contains("brew install libmtp", LibMtpLibrary.NotFoundMessage(isMacOS: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Open_failure_message_on_macOS_mentions_ptpcamerad()
    {
        var message = LibMtpCameraDiscovery.OpenFailedMessage(isMacOS: true);
        Assert.Contains("killall ptpcamerad", message, StringComparison.Ordinal);
        Assert.Contains("Image Capture", message, StringComparison.Ordinal);
    }
}
