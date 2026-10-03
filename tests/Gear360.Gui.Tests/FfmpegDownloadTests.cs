using Gear360.Gui.Services;
using Gear360.Gui.ViewModels;

namespace Gear360.Gui.Tests;

/// <summary>The "ffmpeg is missing" flow: the download is offered, but only runs when the user agrees.</summary>
public sealed class FfmpegDownloadTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeDiscovery _discovery = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeStitchService _stitch = new() { Found = false, DownloadPackage = FakeStitchService.TestPackage };
    private readonly MemorySettingsStore _settings = new();

    public void Dispose() => _temp.Dispose();

    private MainWindowViewModel CreateViewModel() =>
        new(_discovery, _dialogs, _stitch, _settings, new InlineDispatcher())
        {
            Destination = Path.Combine(_temp.Path, "out"),
        };

    private static FakeCameraSource CameraWithMedia()
    {
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/SAM_0001.MP4", 3000);
        camera.Add("DCIM/100PHOTO/SAM_0002.JPG", 1000);
        return camera;
    }

    [Fact]
    public async Task Missing_ffmpeg_shows_the_download_button_with_its_size()
    {
        await using var vm = CreateViewModel();

        Assert.False(vm.IsFfmpegFound);
        Assert.True(vm.ShowFfmpegDownload);
        Assert.Equal("Download ffmpeg (~100 MB)", vm.DownloadFfmpegText);
        Assert.Contains("Test publisher", vm.FfmpegDownloadNote, StringComparison.Ordinal);
        Assert.Contains("GNU GPL v3", vm.FfmpegDownloadNote, StringComparison.Ordinal);
        Assert.Contains("Download ffmpeg", vm.FfmpegStatus, StringComparison.Ordinal);
        Assert.True(vm.DownloadFfmpegCommand.CanExecute(null));
        Assert.Equal(0, _stitch.InstallCount);
    }

    [Fact]
    public async Task Download_button_installs_and_updates_the_status()
    {
        await using var vm = CreateViewModel();

        await vm.DownloadFfmpegCommand.ExecuteAsync(null);

        Assert.Equal(1, _stitch.InstallCount);
        Assert.True(vm.IsFfmpegFound);
        Assert.False(vm.ShowFfmpegDownload);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsDownloadingFfmpeg);
        Assert.StartsWith("ffmpeg found at", vm.FfmpegStatus, StringComparison.Ordinal);
        Assert.Contains(vm.Log, l => l.Contains("ffmpeg installed in", StringComparison.Ordinal));
        Assert.False(vm.DownloadFfmpegCommand.CanExecute(null));
    }

    [Fact]
    public async Task Ticking_stitch_offers_the_download_and_installs_on_yes()
    {
        await using var vm = CreateViewModel();

        vm.StitchVideos = true;
        await vm.FfmpegOfferTask;

        Assert.Equal(1, _dialogs.ConfirmCount);
        Assert.Equal(1, _stitch.InstallCount);
        Assert.True(vm.IsFfmpegFound);
    }

    [Fact]
    public async Task Ticking_stitch_and_declining_downloads_nothing()
    {
        _dialogs.ConfirmAnswer = false;
        await using var vm = CreateViewModel();

        vm.StitchVideos = true;
        await vm.FfmpegOfferTask;

        Assert.Equal(1, _dialogs.ConfirmCount);
        Assert.Equal(0, _stitch.InstallCount);
        Assert.False(vm.IsFfmpegFound);
        Assert.True(vm.StitchVideos);
        Assert.True(vm.ShowFfmpegDownload);
    }

    [Fact]
    public async Task Stitch_loaded_from_settings_does_not_prompt_on_start()
    {
        _settings.Current = new AppSettings { Stitch = true };

        await using var vm = CreateViewModel();
        await vm.FfmpegOfferTask;

        Assert.Equal(0, _dialogs.ConfirmCount);
        Assert.Equal(0, _stitch.InstallCount);
    }

    [Fact]
    public async Task Copy_with_stitch_downloads_ffmpeg_after_confirming_then_copies_and_stitches()
    {
        _settings.Current = new AppSettings { Stitch = true };
        _discovery.Next = () => [CameraWithMedia()];
        await using var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.MediaLoadTask;

        await vm.CopyCommand.ExecuteAsync(null);

        Assert.Equal(1, _dialogs.ConfirmCount);
        Assert.Equal(1, _stitch.InstallCount);
        Assert.Equal(["SAM_0001.MP4"], _stitch.Stitched.Select(Path.GetFileName));
        Assert.Contains(vm.Log, l => l.Contains("Stitching done: 1 stitched", StringComparison.Ordinal));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Copy_with_stitch_and_declined_download_copies_nothing()
    {
        _settings.Current = new AppSettings { Stitch = true };
        _dialogs.ConfirmAnswer = false;
        _discovery.Next = () => [CameraWithMedia()];
        await using var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.MediaLoadTask;

        await vm.CopyCommand.ExecuteAsync(null);

        Assert.Equal(0, _stitch.InstallCount);
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "out")));
        Assert.Contains(vm.Log, l => l.Contains("Cannot stitch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_download_is_logged_and_copies_nothing()
    {
        _settings.Current = new AppSettings { Stitch = true };
        _stitch.InstallFailure = new Gear360.Core.Stitching.FfmpegInstallException("hash mismatch");
        _discovery.Next = () => [CameraWithMedia()];
        await using var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.MediaLoadTask;

        await vm.CopyCommand.ExecuteAsync(null);

        Assert.Equal(1, _stitch.InstallCount);
        Assert.False(vm.IsFfmpegFound);
        Assert.False(vm.IsBusy);
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "out")));
        Assert.Contains(vm.Log, l => l.Contains("ffmpeg download failed: hash mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_download_is_offered_when_there_is_no_package_for_the_platform()
    {
        _stitch.DownloadPackage = null;
        await using var vm = CreateViewModel();

        vm.StitchVideos = true;
        await vm.FfmpegOfferTask;

        Assert.False(vm.ShowFfmpegDownload);
        Assert.False(vm.DownloadFfmpegCommand.CanExecute(null));
        Assert.Equal(0, _dialogs.ConfirmCount);
    }
}
