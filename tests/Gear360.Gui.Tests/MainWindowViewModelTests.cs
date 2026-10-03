using Gear360.Core.Stitching;
using Gear360.Gui.Services;
using Gear360.Gui.ViewModels;

namespace Gear360.Gui.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeDiscovery _discovery = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeStitchService _stitch = new();
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
        camera.Add("DCIM/100PHOTO/SAM_0003.MP4", 2000);
        return camera;
    }

    private static async Task InitializeAsync(MainWindowViewModel vm)
    {
        await vm.InitializeAsync();
        await vm.MediaLoadTask;
    }

    [Fact]
    public async Task Refresh_lists_the_detected_camera_and_ticks_all_its_media()
    {
        var camera = CameraWithMedia();
        _discovery.Next = () => [camera];
        await using var vm = CreateViewModel();

        await InitializeAsync(vm);

        Assert.Same(camera, vm.SelectedSource?.Source);
        Assert.Equal(["SAM_0001.MP4", "SAM_0002.JPG", "SAM_0003.MP4"], vm.Media.Select(m => m.Name));
        Assert.All(vm.Media, m => Assert.True(m.IsSelected));
        Assert.StartsWith("3 of 3 selected", vm.SelectionSummary, StringComparison.Ordinal);
        Assert.True(vm.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task No_camera_shows_a_hint_instead_of_failing()
    {
        await using var vm = CreateViewModel();

        await InitializeAsync(vm);

        Assert.Equal(MainWindowViewModel.NoCameraMessage, vm.StatusMessage);
        Assert.Null(vm.SelectedSource);
        Assert.Empty(vm.Media);
        Assert.False(vm.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Discovery_errors_are_shown_as_a_message()
    {
        _discovery.Next = () => throw new DllNotFoundException("libmtp was not found. Install it with 'brew install libmtp'.");
        await using var vm = CreateViewModel();

        await InitializeAsync(vm);

        Assert.Equal("libmtp was not found. Install it with 'brew install libmtp'.", vm.StatusMessage);
        Assert.False(vm.IsDiscovering);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refresh_disposes_previous_cameras_but_keeps_opened_folders()
    {
        var first = CameraWithMedia();
        var second = CameraWithMedia();
        var calls = 0;
        _discovery.Next = () => ++calls == 1 ? [first] : [second];
        Directory.CreateDirectory(Path.Combine(_temp.Path, "card", "DCIM"));
        await using var vm = CreateViewModel();
        await InitializeAsync(vm);
        await vm.AddFolderSourceAsync(Path.Combine(_temp.Path, "card"));
        await vm.MediaLoadTask;

        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MediaLoadTask;

        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        Assert.Equal(2, vm.Sources.Count);
        Assert.Same(second, vm.Sources[0].Source);
        Assert.True(vm.Sources[1].IsFolder);
        Assert.Same(second, vm.SelectedSource?.Source);

        await vm.DisposeAsync();
        Assert.True(second.IsDisposed);
        Assert.Empty(vm.Sources);
    }

    [Fact]
    public async Task Videos_only_and_none_change_the_ticks()
    {
        _discovery.Next = () => [CameraWithMedia()];
        await using var vm = CreateViewModel();
        await InitializeAsync(vm);

        vm.SelectVideosCommand.Execute(null);
        Assert.Equal([true, false, true], vm.Media.Select(m => m.IsSelected));
        Assert.StartsWith("2 of 3 selected", vm.SelectionSummary, StringComparison.Ordinal);

        vm.SelectNoneCommand.Execute(null);
        Assert.False(vm.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Copy_copies_the_ticked_files_and_remembers_the_destination()
    {
        _discovery.Next = () => [CameraWithMedia()];
        await using var vm = CreateViewModel();
        await InitializeAsync(vm);
        vm.Media[1].IsSelected = false;

        await vm.CopyCommand.ExecuteAsync(null);

        var outDir = new DirectoryInfo(Path.Combine(_temp.Path, "out"));
        var copied = outDir.EnumerateFiles("*", SearchOption.AllDirectories).Select(f => f.Name).Order().ToList();
        Assert.Equal(["SAM_0001.MP4", "SAM_0003.MP4"], copied);
        Assert.False(vm.IsBusy);
        Assert.Equal(100, vm.Progress);
        Assert.Equal(outDir.FullName, vm.LastCopyDestination);
        Assert.True(vm.OpenDestinationCommand.CanExecute(null));
        Assert.Contains(vm.Log, l => l.Contains("Copy done: 2 copied, 0 already there, 0 failed", StringComparison.Ordinal));
        Assert.Equal(outDir.FullName, _settings.Current.LastDestination);
        Assert.Empty(_stitch.Stitched);
    }

    [Fact]
    public async Task Copy_with_stitch_stitches_copied_and_already_present_videos()
    {
        _discovery.Next = () => [CameraWithMedia()];
        await using var vm = CreateViewModel();
        await InitializeAsync(vm);
        vm.Media[2].IsSelected = false;
        await vm.CopyCommand.ExecuteAsync(null);

        vm.Media[2].IsSelected = true;
        vm.StitchVideos = true;
        vm.StitchCodec = StitchCodec.Hevc;
        vm.StitchCrf = 24;
        await vm.CopyCommand.ExecuteAsync(null);

        Assert.Equal(["SAM_0001.MP4", "SAM_0003.MP4"], _stitch.Stitched.Select(Path.GetFileName).Order());
        Assert.Equal(StitchCodec.Hevc, _stitch.LastOptions?.Codec);
        Assert.Equal(24, _stitch.LastOptions?.Crf);
        Assert.Contains(vm.Log, l => l.Contains("Stitching done: 2 stitched, 0 skipped, 0 failed.", StringComparison.Ordinal));
        Assert.True(_settings.Current.Stitch);
        Assert.Equal(StitchCodec.Hevc, _settings.Current.StitchCodec);
        Assert.Equal(StitchEncoder.Auto, _stitch.LastOptions?.Encoder);
        Assert.Contains(vm.Log, l => l.Contains("Stitch encoder: NVIDIA NVENC (hevc_nvenc).", StringComparison.Ordinal));
        Assert.Contains(vm.Log, l => l.Contains("Stitched SAM_0001.MP4 with NVIDIA NVENC ->", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Chosen_encoder_is_used_and_remembered()
    {
        _discovery.Next = () => [CameraWithMedia()];
        await using var vm = CreateViewModel();
        await InitializeAsync(vm);
        vm.StitchVideos = true;
        vm.StitchEncoder = StitchEncoder.Cpu;

        await vm.CopyCommand.ExecuteAsync(null);

        Assert.Equal(StitchEncoder.Cpu, _stitch.LastOptions?.Encoder);
        Assert.Contains(vm.Log, l => l.Contains("Stitch encoder: CPU (libx264).", StringComparison.Ordinal));
        Assert.Equal(StitchEncoder.Cpu, _settings.Current.StitchEncoder);
        Assert.Equal(StitchEncoder.Cpu, new MainWindowViewModel(_discovery, _dialogs, _stitch, _settings, new InlineDispatcher()).StitchEncoder);
    }

    [Fact]
    public async Task An_unusable_encoder_is_reported_before_anything_is_copied()
    {
        _stitch.Unusable.Add(StitchEncoder.Intel);
        _discovery.Next = () => [CameraWithMedia()];
        await using var vm = CreateViewModel();
        await InitializeAsync(vm);
        vm.StitchVideos = true;
        vm.StitchEncoder = StitchEncoder.Intel;

        await vm.CopyCommand.ExecuteAsync(null);

        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "out")));
        Assert.Empty(_stitch.Stitched);
        Assert.Contains(vm.Log, l => l.Contains("Cannot stitch: The Intel Quick Sync encoder cannot be used.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stitch_without_ffmpeg_copies_nothing()
    {
        _stitch.Found = false;
        _discovery.Next = () => [CameraWithMedia()];
        await using var vm = CreateViewModel();
        await InitializeAsync(vm);
        vm.StitchVideos = true;

        await vm.CopyCommand.ExecuteAsync(null);

        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "out")));
        Assert.False(vm.IsFfmpegFound);
        Assert.Contains(vm.Log, l => l.Contains("Cannot stitch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_after_copy_asks_first_and_does_nothing_when_declined()
    {
        var camera = CameraWithMedia();
        _discovery.Next = () => [camera];
        await using var vm = CreateViewModel();
        await InitializeAsync(vm);
        vm.DeleteAfterCopy = true;
        _dialogs.ConfirmAnswer = false;

        await vm.CopyCommand.ExecuteAsync(null);

        Assert.Equal(1, _dialogs.ConfirmCount);
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "out")));
        Assert.Empty(camera.Deleted);
    }

    [Fact]
    public async Task Delete_after_copy_deletes_and_relists_when_confirmed()
    {
        var camera = CameraWithMedia();
        _discovery.Next = () => [camera];
        await using var vm = CreateViewModel();
        await InitializeAsync(vm);
        vm.SelectVideosCommand.Execute(null);
        vm.DeleteAfterCopy = true;

        await vm.CopyCommand.ExecuteAsync(null);
        await vm.MediaLoadTask;

        Assert.Equal(["DCIM/100PHOTO/SAM_0001.MP4", "DCIM/100PHOTO/SAM_0003.MP4"], camera.Deleted);
        Assert.Equal(["SAM_0002.JPG"], vm.Media.Select(m => m.Name));
    }

    [Fact]
    public async Task Opening_a_folder_lists_its_media()
    {
        var card = Path.Combine(_temp.Path, "card");
        Directory.CreateDirectory(Path.Combine(card, "DCIM", "100PHOTO"));
        await File.WriteAllBytesAsync(Path.Combine(card, "DCIM", "100PHOTO", "SAM_0009.MP4"), new byte[10]);
        _dialogs.FolderToPick = card;
        await using var vm = CreateViewModel();
        await InitializeAsync(vm);

        await vm.OpenFolderCommand.ExecuteAsync(null);
        await vm.MediaLoadTask;

        Assert.True(vm.SelectedSource?.IsFolder);
        Assert.Equal(["SAM_0009.MP4"], vm.Media.Select(m => m.Name));

        // Opening the same folder again re-selects it rather than adding a duplicate.
        await vm.OpenFolderCommand.ExecuteAsync(null);
        Assert.Single(vm.Sources);
    }

    [Fact]
    public void Settings_are_loaded_on_start()
    {
        _settings.Current = new AppSettings { LastDestination = "/somewhere/else", StitchCrf = 30, Stitch = true };

        var vm = new MainWindowViewModel(_discovery, _dialogs, _stitch, _settings, new InlineDispatcher());

        Assert.Equal("/somewhere/else", vm.Destination);
        Assert.Equal(30, vm.StitchCrf);
        Assert.True(vm.StitchVideos);
        Assert.True(vm.IsFfmpegFound);
        Assert.StartsWith("ffmpeg found at", vm.FfmpegStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_a_profile_fills_the_advanced_controls()
    {
        var vm = new MainWindowViewModel(_discovery, _dialogs, _stitch, _settings, new InlineDispatcher());
        Assert.Equal(StitchProfile.Balanced, vm.StitchProfile);
        vm.StitchFov = 193;
        vm.StitchEncoder = StitchEncoder.Cpu;

        vm.SelectedProfileChoice = vm.ProfileChoices.Single(c => c.Profile == StitchProfile.Max);

        Assert.Equal(StitchProfile.Max, vm.StitchProfile);
        Assert.Equal(StitchCodec.Hevc, vm.StitchCodec);
        Assert.Equal(14, vm.StitchCrf);
        Assert.Equal("slow", vm.StitchPreset);
        Assert.Equal(StitchInterpolation.Lanczos, vm.StitchInterpolation);
        Assert.True(vm.StitchHighQualityTuning);
        Assert.Equal(StitchProfiles.Describe(StitchProfile.Max), vm.StitchProfileDescription);

        // Lens and encoder settings are not part of a profile.
        Assert.Equal(193, vm.StitchFov);
        Assert.Equal(StitchEncoder.Cpu, vm.StitchEncoder);

        vm.StitchProfile = StitchProfile.Fast;
        Assert.Equal((StitchCodec.H264, 23m, "veryfast", StitchInterpolation.Linear, false), (vm.StitchCodec, vm.StitchCrf!.Value, vm.StitchPreset, vm.StitchInterpolation, vm.StitchHighQualityTuning));

        // Custom keeps whatever the controls hold.
        vm.StitchProfile = StitchProfile.Custom;
        Assert.Equal(23, vm.StitchCrf);
        Assert.Equal("veryfast", vm.StitchPreset);
    }

    [Fact]
    public void Editing_an_advanced_quality_control_switches_to_custom()
    {
        var vm = new MainWindowViewModel(_discovery, _dialogs, _stitch, _settings, new InlineDispatcher());
        vm.StitchProfile = StitchProfile.High;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.StitchCrf = 18;

        Assert.Equal(StitchProfile.Custom, vm.StitchProfile);
        Assert.Equal(StitchProfile.Custom, vm.SelectedProfileChoice.Profile);
        Assert.Contains(nameof(vm.SelectedProfileChoice), changed);
        Assert.Equal(18, vm.StitchCrf);
        Assert.Equal("slow", vm.StitchPreset);

        // Back to High's values: High again.
        vm.StitchCrf = 16;
        Assert.Equal(StitchProfile.High, vm.StitchProfile);

        foreach (var edit in new Action[]
        {
            () => vm.StitchCodec = StitchCodec.Hevc,
            () => vm.StitchPreset = "medium",
            () => vm.StitchInterpolation = StitchInterpolation.Cubic,
            () => vm.StitchHighQualityTuning = false,
        })
        {
            vm.StitchProfile = StitchProfile.High;
            edit();
            Assert.Equal(StitchProfile.Custom, vm.StitchProfile);
        }

        // Lens, encoder and audio settings leave the profile alone.
        vm.StitchProfile = StitchProfile.High;
        vm.StitchFov = 190;
        vm.StitchYaw = 45;
        vm.StitchEncoder = StitchEncoder.Cpu;
        vm.StitchAudio = StitchAudio.Aac;
        Assert.Equal(StitchProfile.High, vm.StitchProfile);
    }

    [Fact]
    public async Task Stitching_uses_the_profile_and_remembers_it()
    {
        _discovery.Next = () => [CameraWithMedia()];
        await using var vm = CreateViewModel();
        await InitializeAsync(vm);
        vm.StitchVideos = true;
        vm.StitchProfile = StitchProfile.High;
        vm.StitchAudio = StitchAudio.Aac;
        vm.StitchRoll = -2;

        await vm.CopyCommand.ExecuteAsync(null);

        Assert.Equal(StitchProfiles.Get(StitchProfile.High) with { Audio = StitchAudio.Aac, Roll = -2 }, _stitch.LastOptions);
        Assert.Contains(vm.Log, l => l.Contains("Stitch quality High: h264_nvenc, CQ 21 (CRF 16), preset p7, HQ tuning, interp line, audio AAC 192k", StringComparison.Ordinal));
        Assert.Equal(StitchProfile.High, _settings.Current.StitchProfile);

        var reloaded = new MainWindowViewModel(_discovery, _dialogs, _stitch, _settings, new InlineDispatcher());
        Assert.Equal(StitchProfile.High, reloaded.StitchProfile);
        Assert.Equal(StitchAudio.Aac, reloaded.StitchAudio);
        Assert.Equal(-2, reloaded.StitchRoll);
        Assert.Equal(_stitch.LastOptions, reloaded.BuildStitchOptions());
    }

    [Fact]
    public async Task Custom_settings_are_remembered_as_they_are()
    {
        _discovery.Next = () => [CameraWithMedia()];
        await using var vm = CreateViewModel();
        await InitializeAsync(vm);
        vm.StitchVideos = true;
        vm.StitchProfile = StitchProfile.Max;
        vm.StitchPreset = "veryslow";
        vm.StitchInterpolation = StitchInterpolation.Spline16;

        await vm.CopyCommand.ExecuteAsync(null);

        Assert.Equal(StitchProfile.Custom, _settings.Current.StitchProfile);
        var reloaded = new MainWindowViewModel(_discovery, _dialogs, _stitch, _settings, new InlineDispatcher());
        Assert.Equal(StitchProfile.Custom, reloaded.StitchProfile);
        Assert.Equal(vm.BuildStitchOptions(), reloaded.BuildStitchOptions());
        Assert.Equal(StitchProfiles.Get(StitchProfile.Max) with { Preset = "veryslow", Interpolation = StitchInterpolation.Spline16 }, reloaded.BuildStitchOptions());
    }

    [Fact]
    public void A_saved_named_profile_uses_the_profiles_current_values()
    {
        _settings.Current = new AppSettings { StitchProfile = StitchProfile.Max, StitchCrf = 30, StitchCodec = StitchCodec.H264 };

        var vm = new MainWindowViewModel(_discovery, _dialogs, _stitch, _settings, new InlineDispatcher());

        Assert.Equal(StitchProfile.Max, vm.StitchProfile);
        Assert.Equal(14, vm.StitchCrf);
        Assert.Equal(StitchCodec.Hevc, vm.StitchCodec);
    }

    [Fact]
    public void Settings_from_before_profiles_get_the_matching_profile()
    {
        _settings.Current = new AppSettings();
        Assert.Equal(StitchProfile.Balanced, new MainWindowViewModel(_discovery, _dialogs, _stitch, _settings, new InlineDispatcher()).StitchProfile);

        _settings.Current = new AppSettings { StitchCrf = 23, StitchPreset = "veryfast" };
        Assert.Equal(StitchProfile.Fast, new MainWindowViewModel(_discovery, _dialogs, _stitch, _settings, new InlineDispatcher()).StitchProfile);

        _settings.Current = new AppSettings { StitchCrf = 30, StitchPreset = "not a preset" };
        var vm = new MainWindowViewModel(_discovery, _dialogs, _stitch, _settings, new InlineDispatcher());
        Assert.Equal(StitchProfile.Custom, vm.StitchProfile);
        Assert.Equal(30, vm.StitchCrf);
        Assert.Equal("medium", vm.StitchPreset);
    }

    [Fact]
    public void Default_destination_ends_in_gear_360() =>
        Assert.Equal("Gear 360", Path.GetFileName(MainWindowViewModel.DefaultDestination()));
}
