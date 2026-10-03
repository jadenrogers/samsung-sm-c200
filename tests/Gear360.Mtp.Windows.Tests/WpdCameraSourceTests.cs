using System.Runtime.InteropServices;

using Gear360.Core;
using Gear360.Core.Import;

namespace Gear360.Mtp.Windows.Tests;

public sealed class WpdCameraSourceTests : IDisposable
{
    private readonly StaTaskScheduler _scheduler = new("test worker");
    private readonly FakeWpdDevice _device = new(DeviceMatcherTests.Gear360);

    public void Dispose() => _scheduler.Dispose();

    [Fact]
    public async Task Lists_files_with_object_ids_paths_sizes_and_local_dates()
    {
        var taken = new DateTime(2016, 7, 1, 14, 30, 0, DateTimeKind.Unspecified);
        var video = _device.Add("DCIM/100PHOTO/SAM_0001.MP4", 5000, taken);
        _device.Add("DCIM/100PHOTO/SAM_0002.JPG", 10);
        await using var source = CreateSource();

        var files = await ToListAsync(source.EnumerateMediaAsync());

        Assert.Equal(2, files.Count);
        Assert.Equal(new CameraFile(video.Id, "DCIM/100PHOTO/SAM_0001.MP4", 5000, new DateTimeOffset(taken)), files[0]);
        Assert.Null(files[1].Modified);
        Assert.Equal(MediaKind.Video, files[0].Kind);
    }

    [Fact]
    public async Task Copies_all_bytes_and_reports_cumulative_progress()
    {
        var entry = _device.Add("DCIM/100PHOTO/SAM_0001.MP4", 4321);
        await using var source = CreateSource();
        var file = (await ToListAsync(source.EnumerateMediaAsync())).Single();
        var reports = new List<long>();
        using var output = new MemoryStream();

        await source.CopyToAsync(file, output, new SyncProgress(reports.Add));

        Assert.Equal(_device.ContentOf(entry.Id), output.ToArray());
        Assert.Equal(4321, reports[^1]);
        Assert.True(reports.Count > 1);
        Assert.Equal(reports.Order(), reports);
    }

    [Fact]
    public async Task Every_device_call_runs_on_the_worker_thread()
    {
        _device.Add("DCIM/100PHOTO/SAM_0001.MP4", 3000);
        var source = CreateSource();
        var file = (await ToListAsync(source.EnumerateMediaAsync())).Single();
        await source.CopyToAsync(file, Stream.Null);
        await source.DeleteAsync(file);
        await source.DisposeAsync();

        Assert.Equal([_scheduler.ThreadId], _device.CallingThreads);
    }

    [Fact]
    public async Task Disconnect_mid_copy_becomes_a_friendly_WpdDeviceException()
    {
        _device.Add("DCIM/100PHOTO/SAM_0001.MP4", 5000);
        _device.ReadFailure = new COMException("A device attached to the system is not functioning.", unchecked((int)0x8007001F));
        await using var source = CreateSource();
        var file = (await ToListAsync(source.EnumerateMediaAsync())).Single();

        var ex = await Assert.ThrowsAsync<WpdDeviceException>(() => source.CopyToAsync(file, Stream.Null));

        Assert.Equal(WpdErrorKind.Disconnected, ex.Kind);
        Assert.Contains("copy SAM_0001.MP4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_honours_cancellation()
    {
        _device.Add("DCIM/100PHOTO/SAM_0001.MP4", 5000);
        await using var source = CreateSource();
        var file = (await ToListAsync(source.EnumerateMediaAsync())).Single();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.CopyToAsync(file, Stream.Null, new SyncProgress(_ => cts.Cancel()), cts.Token));
    }

    [Fact]
    public async Task Delete_removes_the_file_from_a_recognised_camera()
    {
        var entry = _device.Add("DCIM/100PHOTO/SAM_0001.MP4", 10);
        await using var source = CreateSource();
        var file = (await ToListAsync(source.EnumerateMediaAsync())).Single();

        await source.DeleteAsync(file);

        Assert.Equal([entry.Id], _device.Deleted);
    }

    [Fact]
    public async Task Delete_is_refused_on_a_fallback_device()
    {
        var phone = new FakeWpdDevice(DeviceMatcherTests.SamsungPhone);
        phone.Add("DCIM/Camera/20240101_000000.mp4", 10);
        await using var source = new WpdCameraSource(phone, phone.Info, DeviceMatch.Fallback, _scheduler);
        var file = (await ToListAsync(source.EnumerateMediaAsync())).Single();

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.DeleteAsync(file));

        Assert.Empty(phone.Deleted);
        Assert.False(source.IsRecognizedCamera);
        Assert.EndsWith(DeviceMatcher.FallbackSuffix, source.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_file_is_reported_as_not_found()
    {
        await using var source = CreateSource();

        var ex = await Assert.ThrowsAsync<WpdDeviceException>(() =>
            source.CopyToAsync(new CameraFile("nope", "DCIM/X.MP4", 1, null), Stream.Null));

        Assert.Equal(WpdErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Dispose_closes_the_device_once()
    {
        var source = CreateSource();

        await source.DisposeAsync();
        await source.DisposeAsync();

        Assert.Equal(1, _device.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ToListAsync(source.EnumerateMediaAsync()));
    }

    [Fact]
    public async Task Works_end_to_end_with_the_import_service()
    {
        _device.Add("DCIM/100PHOTO/SAM_0001.MP4", 3000, new DateTime(2016, 7, 1, 12, 0, 0));
        _device.Add("DCIM/100PHOTO/SAM_0002.JPG", 200, new DateTime(2016, 7, 2, 12, 0, 0));
        await using var source = CreateSource();
        var destination = Path.Combine(Path.GetTempPath(), "gear360-wpd-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new ImportService().ImportAsync(source, new ImportOptions { Destination = destination, DeleteAfter = true });

            Assert.True(result.Succeeded);
            Assert.Equal(2, result.CopiedPaths.Count);
            Assert.Equal(2, result.DeletedCount);
            Assert.Equal(3000, new FileInfo(Path.Combine(destination, "2016-07-01", "SAM_0001.MP4")).Length);
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    private WpdCameraSource CreateSource() => new(_device, _device.Info, DeviceMatch.Recognized, _scheduler);

    private static async Task<List<CameraFile>> ToListAsync(IAsyncEnumerable<CameraFile> files)
    {
        var list = new List<CameraFile>();
        await foreach (var file in files)
        {
            list.Add(file);
        }

        return list;
    }

    private sealed class SyncProgress(Action<long> handler) : IProgress<long>
    {
        public void Report(long value) => handler(value);
    }
}
