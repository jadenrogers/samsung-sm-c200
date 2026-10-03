using System.Runtime.InteropServices;

namespace Gear360.Mtp.Windows.Tests;

public sealed class WpdCameraDiscoveryTests : IDisposable
{
    private readonly StaTaskScheduler _scheduler = new("test worker");

    public void Dispose() => _scheduler.Dispose();

    [Fact]
    public async Task Returns_the_Gear_360_and_releases_the_phone()
    {
        var camera = new FakeWpdDevice(DeviceMatcherTests.Gear360);
        var phone = new FakeWpdDevice(DeviceMatcherTests.SamsungPhone);

        var sources = await Discover(phone, camera).DiscoverAsync();

        var source = Assert.IsType<WpdCameraSource>(Assert.Single(sources));
        Assert.Equal("Gear 360 (SM-C200)", source.DisplayName);
        Assert.True(source.IsRecognizedCamera);
        Assert.True(camera.Connected);
        Assert.Equal(0, camera.DisposeCount);
        Assert.Equal(1, phone.DisposeCount);
        await source.DisposeAsync();
        Assert.Equal(1, camera.DisposeCount);
    }

    [Fact]
    public async Task Offers_a_flagged_fallback_when_no_Gear_360_is_connected()
    {
        var phone = new FakeWpdDevice(DeviceMatcherTests.SamsungPhone);
        var player = new FakeWpdDevice(new WpdDeviceInfo("Music player", null, "Acme", null, null), hasDcim: false);

        var sources = await Discover(phone, player).DiscoverAsync();

        var source = Assert.IsType<WpdCameraSource>(Assert.Single(sources));
        Assert.False(source.IsRecognizedCamera);
        Assert.Equal("Galaxy S23 (SM-S911B) - not recognised as a Gear 360", source.DisplayName);
        Assert.Equal(1, player.DisposeCount);
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Fallbacks_can_be_turned_off()
    {
        var phone = new FakeWpdDevice(DeviceMatcherTests.SamsungPhone);
        var discovery = new WpdCameraDiscovery(() => [phone], _scheduler) { IncludeUnrecognizedDevices = false };

        Assert.Empty(await discovery.DiscoverAsync());
        Assert.Equal(1, phone.DisposeCount);
    }

    [Fact]
    public async Task Busy_Gear_360_is_reported_instead_of_no_camera()
    {
        var camera = new FakeWpdDevice(DeviceMatcherTests.Gear360)
        {
            ConnectFailure = new COMException("The requested resource is in use.", unchecked((int)0x800700AA)),
        };
        var phone = new FakeWpdDevice(DeviceMatcherTests.SamsungPhone);

        var ex = await Assert.ThrowsAsync<WpdDeviceException>(() => Discover(camera, phone).DiscoverAsync());

        Assert.Equal(WpdErrorKind.Busy, ex.Kind);
        Assert.StartsWith("Gear 360 (SM-C200) is busy", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, camera.DisposeCount);
        Assert.Equal(1, phone.DisposeCount);
    }

    [Fact]
    public async Task Locked_phone_is_skipped_quietly()
    {
        var phone = new FakeWpdDevice(DeviceMatcherTests.SamsungPhone)
        {
            ConnectFailure = new COMException("Access is denied.", unchecked((int)0x80070005)),
        };

        Assert.Empty(await Discover(phone).DiscoverAsync());
        Assert.Equal(1, phone.DisposeCount);
    }

    [Fact]
    public async Task No_devices_means_no_cameras()
    {
        Assert.Empty(await Discover().DiscoverAsync());
    }

    [Fact]
    public async Task Missing_WPD_is_reported_as_unavailable()
    {
        var discovery = new WpdCameraDiscovery(() => throw new COMException("Class not registered", unchecked((int)0x80040154)), _scheduler);

        var ex = await Assert.ThrowsAsync<WpdDeviceException>(() => discovery.DiscoverAsync());

        Assert.Equal(WpdErrorKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task Probing_runs_on_the_worker_thread()
    {
        var camera = new FakeWpdDevice(DeviceMatcherTests.Gear360);

        var sources = await Task.Run(() => Discover(camera).DiscoverAsync());
        await sources[0].DisposeAsync();

        Assert.Equal([_scheduler.ThreadId], camera.CallingThreads);
    }

    [Fact]
    public async Task Cancellation_releases_every_device()
    {
        var camera = new FakeWpdDevice(DeviceMatcherTests.Gear360);
        using var cts = new CancellationTokenSource();
        var discovery = new WpdCameraDiscovery(
            () =>
            {
                cts.Cancel();
                return [camera];
            },
            _scheduler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.DiscoverAsync(cts.Token));

        Assert.Equal(1, camera.DisposeCount);
    }

    private WpdCameraDiscovery Discover(params IWpdDevice[] devices) => new(() => devices, _scheduler);
}
