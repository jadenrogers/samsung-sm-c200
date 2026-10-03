using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gear360.Core;
using Gear360.Core.Import;
using Gear360.Core.Stitching;
using Gear360.Gui.Services;

namespace Gear360.Gui.ViewModels;

/// <summary>State and commands of the main window: pick a source, tick files, copy (and optionally stitch) them.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>Shown when discovery finds no camera.</summary>
    public const string NoCameraMessage =
        "No Gear 360 found. Switch the camera on and connect it with a USB data cable, " +
        "or take out the microSD card and use \"Open folder / SD card...\".";

    private const int MaxLogLines = 2000;

    private readonly ICameraDiscovery _discovery;
    private readonly IDialogService _dialogs;
    private readonly IStitchService _stitch;
    private readonly ISettingsStore _settingsStore;
    private readonly IUiDispatcher _dispatcher;
    private readonly ImportService _importService = new();

    // Progress from worker threads is queued here and applied on the UI thread, in order.
    private readonly ConcurrentQueue<string> _pendingLog = new();
    private ImportProgress? _latestImport;
    private double? _latestStitchFraction;
    private int _drainPosted;

    private Task _mediaLoad = Task.CompletedTask;
    private CancellationTokenSource? _mediaLoadCts;
    private CancellationTokenSource? _copyCts;
    private long[] _bytesBefore = [];
    private long _totalBytes;
    private string _stitchPrefix = string.Empty;
    private bool _disposed;
    private bool _ready;
    private bool _applyingProfile;
    private (long Done, long? Total)? _latestDownload;

    /// <summary>Creates the view model. Settings are loaded straight away; call <see cref="InitializeAsync"/> to look for cameras.</summary>
    public MainWindowViewModel(
        ICameraDiscovery discovery,
        IDialogService dialogs,
        IStitchService stitch,
        ISettingsStore settingsStore,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(stitch);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _discovery = discovery;
        _dialogs = dialogs;
        _stitch = stitch;
        _settingsStore = settingsStore;
        _dispatcher = dispatcher;

        var settings = settingsStore.Load();
        Destination = string.IsNullOrWhiteSpace(settings.LastDestination) ? DefaultDestination() : settings.LastDestination;
        FfmpegPath = settings.FfmpegPath;
        StitchVideos = settings.Stitch;
        StitchFov = (decimal)settings.StitchFov;
        StitchYaw = (decimal)settings.StitchYaw;
        StitchPitch = (decimal)settings.StitchPitch;
        StitchRoll = (decimal)settings.StitchRoll;
        StitchEncoder = Enum.IsDefined(settings.StitchEncoder) ? settings.StitchEncoder : StitchEncoder.Auto;
        StitchAudio = Enum.IsDefined(settings.StitchAudio) ? settings.StitchAudio : StitchAudio.Copy;
        var saved = new StitchOptions
        {
            Codec = Enum.IsDefined(settings.StitchCodec) ? settings.StitchCodec : StitchCodec.H264,
            Crf = settings.StitchCrf,
            Preset = StitchOptions.Presets.Contains(settings.StitchPreset) ? settings.StitchPreset : "medium",
            Interpolation = Enum.IsDefined(settings.StitchInterpolation) ? settings.StitchInterpolation : StitchInterpolation.Linear,
            HighQualityEncoderTuning = settings.StitchHighQualityTuning,
        };

        // A named profile always means its current settings; settings saved before profiles existed get the profile
        // their values match, else Custom.
        var profile = settings.StitchProfile is { } chosen && Enum.IsDefined(chosen) ? chosen : StitchProfiles.Match(saved);
        ShowQualitySettings(StitchProfiles.ApplyTo(saved, profile));
        StitchProfile = profile;
        FfmpegStatus = string.Empty;
        ProgressText = "Ready.";
        SelectionSummary = "No files.";
        UpdateFfmpegStatus(_stitch.TryLocate(FfmpegPath));
        _ready = true;
    }

    /// <summary>Detected cameras followed by folders the user opened.</summary>
    public ObservableCollection<SourceItem> Sources { get; } = [];

    /// <summary>The media on the selected source.</summary>
    public ObservableCollection<MediaItemViewModel> Media { get; } = [];

    /// <summary>Lines of the activity log, oldest first.</summary>
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>The codecs offered for stitching.</summary>
    public IReadOnlyList<StitchCodec> Codecs { get; } = Enum.GetValues<StitchCodec>();

    /// <summary>The quality profiles offered, Custom last.</summary>
    public IReadOnlyList<StitchProfileChoice> ProfileChoices { get; } = [.. Enum.GetValues<StitchProfile>().Select(p => new StitchProfileChoice(p))];

    /// <summary>The x264/x265 presets offered, fastest first.</summary>
    public IReadOnlyList<string> Presets => StitchOptions.Presets;

    /// <summary>The v360 interpolation methods offered.</summary>
    public IReadOnlyList<StitchInterpolation> Interpolations { get; } = Enum.GetValues<StitchInterpolation>();

    /// <summary>The audio choices offered.</summary>
    public IReadOnlyList<StitchAudio> AudioModes { get; } = Enum.GetValues<StitchAudio>();

    /// <summary>
    /// The quality profile. Choosing a named profile fills the Advanced controls it covers; editing one of them
    /// switches to the profile the settings now match, which is Custom unless they happen to equal another profile.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProfileChoice), nameof(StitchProfileDescription))]
    public partial StitchProfile StitchProfile { get; set; }

    /// <summary>The dropdown entry for <see cref="StitchProfile"/>.</summary>
    public StitchProfileChoice SelectedProfileChoice
    {
        get => ProfileChoices.First(c => c.Profile == StitchProfile);
        set
        {
            if (value is not null)
            {
                StitchProfile = value.Profile;
            }
        }
    }

    /// <summary>The one-line description of the chosen profile.</summary>
    public string StitchProfileDescription => StitchProfiles.Describe(StitchProfile);

    /// <summary>The x264/x265 preset (mapped to the nearest one for hardware encoders).</summary>
    [ObservableProperty]
    public partial string StitchPreset { get; set; } = "medium";

    /// <summary>How v360 samples the fisheye pictures.</summary>
    [ObservableProperty]
    public partial StitchInterpolation StitchInterpolation { get; set; }

    /// <summary>Use the hardware encoder's slowest preset and quality features.</summary>
    [ObservableProperty]
    public partial bool StitchHighQualityTuning { get; set; }

    /// <summary>Copy the sound, or re-encode it to AAC.</summary>
    [ObservableProperty]
    public partial StitchAudio StitchAudio { get; set; }

    /// <summary>Yaw of the stitched view, in degrees.</summary>
    [ObservableProperty]
    public partial decimal? StitchYaw { get; set; }

    /// <summary>Pitch of the stitched view, in degrees.</summary>
    [ObservableProperty]
    public partial decimal? StitchPitch { get; set; }

    /// <summary>Roll of the stitched view, in degrees.</summary>
    [ObservableProperty]
    public partial decimal? StitchRoll { get; set; }

    /// <summary>The encoders offered for stitching, with Auto first.</summary>
    public IReadOnlyList<StitchEncoder> Encoders { get; } = Enum.GetValues<StitchEncoder>();

    /// <summary>Who encodes stitched video: Auto uses a working graphics card encoder, else the CPU.</summary>
    [ObservableProperty]
    public partial StitchEncoder StitchEncoder { get; set; }

    /// <summary>The source whose media is listed.</summary>
    [ObservableProperty]
    public partial SourceItem? SelectedSource { get; set; }

    /// <summary>A one-line message about discovery or listing (errors and hints).</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    /// <summary>True while cameras are being looked for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeSource))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(OpenFolderCommand))]
    public partial bool IsDiscovering { get; set; }

    /// <summary>True while the media list is being read.</summary>
    [ObservableProperty]
    public partial bool IsLoadingMedia { get; set; }

    /// <summary>The folder files are copied into (each into a <c>yyyy-MM-dd</c> subfolder).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    public partial string Destination { get; set; }

    /// <summary>Stitch copied videos into 360 files.</summary>
    [ObservableProperty]
    public partial bool StitchVideos { get; set; }

    /// <summary>Lens field of view, in degrees.</summary>
    [ObservableProperty]
    public partial decimal? StitchFov { get; set; }

    /// <summary>The stitch output codec.</summary>
    [ObservableProperty]
    public partial StitchCodec StitchCodec { get; set; }

    /// <summary>The stitch quality: lower is better and larger.</summary>
    [ObservableProperty]
    public partial decimal? StitchCrf { get; set; }

    /// <summary>An ffmpeg location the user chose; null to search PATH.</summary>
    [ObservableProperty]
    public partial string? FfmpegPath { get; set; }

    /// <summary>Where ffmpeg was found, or how to install it.</summary>
    [ObservableProperty]
    public partial string FfmpegStatus { get; set; }

    /// <summary>True when ffmpeg and ffprobe were found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFfmpegDownload))]
    [NotifyCanExecuteChangedFor(nameof(DownloadFfmpegCommand))]
    public partial bool IsFfmpegFound { get; set; }

    /// <summary>True while ffmpeg is being downloaded.</summary>
    [ObservableProperty]
    public partial bool IsDownloadingFfmpeg { get; set; }

    /// <summary>True when ffmpeg is missing and can be downloaded for this machine.</summary>
    public bool ShowFfmpegDownload => !IsFfmpegFound && _stitch.DownloadPackage is not null;

    /// <summary>The download button's text, e.g. "Download ffmpeg (~110 MB)".</summary>
    public string DownloadFfmpegText =>
        _stitch.DownloadPackage is { } package
            ? string.Create(CultureInfo.CurrentCulture, $"Download ffmpeg (~{package.TotalSize / (1024 * 1024)} MB)")
            : "Download ffmpeg";

    /// <summary>Where the download comes from and its license, shown next to the button.</summary>
    public string FfmpegDownloadNote =>
        _stitch.DownloadPackage is { } package
            ? $"Downloads ffmpeg {package.Version} directly from {package.Publisher}. ffmpeg is licensed under the {package.License} " +
              "and is not part of or redistributed by this app."
            : string.Empty;

    /// <summary>The offer to download ffmpeg made when stitching was ticked (for tests).</summary>
    internal Task FfmpegOfferTask { get; private set; } = Task.CompletedTask;

    /// <summary>Delete each file from the camera once it has been copied and verified.</summary>
    [ObservableProperty]
    public partial bool DeleteAfterCopy { get; set; }

    /// <summary>True while a copy (or the stitching after it) runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanChangeSource))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand), nameof(CancelCommand), nameof(RefreshCommand), nameof(OpenFolderCommand), nameof(OpenDestinationCommand), nameof(DownloadFfmpegCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Overall progress of the current phase, 0 to 100.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>What is happening now, e.g. the file being copied.</summary>
    [ObservableProperty]
    public partial string ProgressText { get; set; }

    /// <summary>"3 of 10 selected (1.2 GB)".</summary>
    [ObservableProperty]
    public partial string SelectionSummary { get; set; }

    /// <summary>The destination of the last finished copy; enables "Open destination folder".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenDestinationCommand))]
    public partial string? LastCopyDestination { get; set; }

    /// <summary>True when no copy is running.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>True when the source can be changed (nothing copying, no discovery running).</summary>
    public bool CanChangeSource => !IsBusy && !IsDiscovering;

    /// <summary>The media listing started by the last source change (for tests and shutdown).</summary>
    internal Task MediaLoadTask => _mediaLoad;

    /// <summary>The default destination: the user's Videos (or Pictures) folder plus <c>Gear 360</c>.</summary>
    public static string DefaultDestination()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.UserProfile })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path))
            {
                return Path.Combine(path, "Gear 360");
            }
        }

        return Path.Combine(Path.GetTempPath(), "Gear 360");
    }

    /// <summary>Looks for cameras the first time the window opens.</summary>
    public Task InitializeAsync() => RefreshAsync();

    /// <summary>Cancels running work and disposes every source.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_copyCts is { } copy)
        {
            await copy.CancelAsync().ConfigureAwait(true);
        }

        if (CopyCommand.ExecutionTask is { } running)
        {
            await IgnoreFailure(running).ConfigureAwait(true);
        }

        await StopMediaLoadAsync().ConfigureAwait(true);
        var sources = Sources.ToList();
        Sources.Clear();
        foreach (var item in sources)
        {
            await DisposeQuietlyAsync(item.Source).ConfigureAwait(true);
        }
    }

    /// <summary>Looks for cameras again. Previously found cameras are disposed; opened folders stay.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSource))]
    private async Task RefreshAsync()
    {
        IsDiscovering = true;
        StatusMessage = "Looking for cameras...";
        try
        {
            var previous = SelectedSource;
            var oldCameras = Sources.Where(s => !s.IsFolder).ToList();
            if (previous is not null && oldCameras.Contains(previous))
            {
                SelectedSource = null;
            }

            // Wait for any listing to stop before disposing the camera it may be reading.
            await IgnoreFailure(_mediaLoad).ConfigureAwait(true);
            foreach (var item in oldCameras)
            {
                Sources.Remove(item);
                await DisposeQuietlyAsync(item.Source).ConfigureAwait(true);
            }

            IReadOnlyList<ICameraSource> cameras;
            string? error = null;
            try
            {
                cameras = await Task.Run(() => _discovery.DiscoverAsync(CancellationToken.None)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Backends report a missing native library or a device that cannot be opened with a readable message.
                error = ex.Message;
                cameras = [];
            }

            if (_disposed)
            {
                foreach (var camera in cameras)
                {
                    await DisposeQuietlyAsync(camera).ConfigureAwait(true);
                }

                return;
            }

            for (var i = 0; i < cameras.Count; i++)
            {
                Sources.Insert(i, new SourceItem(cameras[i], IsFolder: false));
            }

            StatusMessage = error
                ?? (cameras.Count == 0
                    ? NoCameraMessage
                    : string.Create(CultureInfo.CurrentCulture, $"Found {cameras.Count} camera{(cameras.Count == 1 ? string.Empty : "s")}."));

            if (cameras.Count > 0)
            {
                SelectedSource = Sources[0];
            }
            else if (SelectedSource is null && Sources.Count > 0)
            {
                SelectedSource = Sources[0];
            }
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    /// <summary>Lets the user pick a folder (such as a microSD card) and reads media from it.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSource))]
    private async Task OpenFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose the camera's microSD card or a folder with DCIM", null).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        await AddFolderSourceAsync(folder).ConfigureAwait(true);
    }

    /// <summary>Adds (or re-selects) a folder source.</summary>
    internal async Task AddFolderSourceAsync(string folder)
    {
        FolderCameraSource source;
        try
        {
            source = new FolderCameraSource(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusMessage = ex.Message;
            return;
        }

        var existing = Sources.FirstOrDefault(s => s.Source is FolderCameraSource f &&
            string.Equals(f.RootPath, source.RootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (existing is not null)
        {
            await source.DisposeAsync().ConfigureAwait(true);
            SelectedSource = existing;
            return;
        }

        var item = new SourceItem(source, IsFolder: true);
        Sources.Add(item);
        StatusMessage = null;
        SelectedSource = item;
    }

    partial void OnSelectedSourceChanged(SourceItem? value)
    {
        var previous = _mediaLoad;
        _mediaLoadCts?.Cancel();
        _mediaLoadCts?.Dispose();
        _mediaLoadCts = new CancellationTokenSource();
        _mediaLoad = LoadMediaAsync(previous, value, _mediaLoadCts.Token);
    }

    private async Task LoadMediaAsync(Task previous, SourceItem? item, CancellationToken cancellationToken)
    {
        await IgnoreFailure(previous).ConfigureAwait(true);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        Media.Clear();
        UpdateSelectionSummary();
        IsLoadingMedia = item is not null;
        if (item is null)
        {
            return;
        }

        try
        {
            var files = await Task.Run(
                async () =>
                {
                    var list = new List<CameraFile>();
                    await foreach (var file in item.Source.EnumerateMediaAsync(cancellationToken).ConfigureAwait(false))
                    {
                        list.Add(file);
                    }

                    return list;
                },
                cancellationToken).ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            foreach (var file in files)
            {
                Media.Add(new MediaItemViewModel(file, OnItemSelectionChanged) { IsSelected = true });
            }

            if (files.Count == 0 && item.IsFolder)
            {
                StatusMessage = $"No photos or videos found in {item.DisplayName}.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Another source was chosen.
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not read {item.DisplayName}: {ex.Message}";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoadingMedia = false;
            }

            UpdateSelectionSummary();
        }
    }

    private async Task StopMediaLoadAsync()
    {
        _mediaLoadCts?.Cancel();
        await IgnoreFailure(_mediaLoad).ConfigureAwait(true);
        IsLoadingMedia = false;
    }

    /// <summary>Ticks every file.</summary>
    [RelayCommand]
    private void SelectAll() => SetSelection(_ => true);

    /// <summary>Unticks every file.</summary>
    [RelayCommand]
    private void SelectNone() => SetSelection(_ => false);

    /// <summary>Ticks videos and unticks photos.</summary>
    [RelayCommand]
    private void SelectVideos() => SetSelection(m => m.IsVideo);

    private void SetSelection(Func<MediaItemViewModel, bool> selected)
    {
        foreach (var item in Media)
        {
            item.IsSelected = selected(item);
        }
    }

    private void OnItemSelectionChanged() => UpdateSelectionSummary();

    private void UpdateSelectionSummary()
    {
        var selected = Media.Where(m => m.IsSelected).ToList();
        SelectionSummary = Media.Count == 0
            ? "No files."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{selected.Count} of {Media.Count} selected ({ByteSize.Format(selected.Sum(m => m.File.Size))})");
        CopyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Lets the user choose the destination folder.</summary>
    [RelayCommand]
    private async Task BrowseDestinationAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose where to copy the files", Directory.Exists(Destination) ? Destination : null).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Destination = folder;
        }
    }

    /// <summary>Lets the user point at an ffmpeg executable.</summary>
    [RelayCommand]
    private async Task BrowseFfmpegAsync()
    {
        var file = await _dialogs.PickFileAsync("Choose the ffmpeg executable").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        var tools = _stitch.TryLocate(file);
        if (tools is null)
        {
            FfmpegStatus = $"ffmpeg and ffprobe were not found at {file}. Pick ffmpeg from a folder that also has ffprobe.";
            IsFfmpegFound = false;
            return;
        }

        FfmpegPath = file;
        UpdateFfmpegStatus(tools);
        SaveSettings();
    }

    private void UpdateFfmpegStatus(FfmpegTools? tools)
    {
        IsFfmpegFound = tools is not null;
        FfmpegStatus = tools is not null
            ? $"ffmpeg found at {tools.FfmpegPath}"
            : _stitch.DownloadPackage is not null
                ? $"ffmpeg was not found; it is needed to stitch. Use \"{DownloadFfmpegText}\". Or: {FfmpegLocator.InstallHint} Then restart this app, or use \"Browse for ffmpeg...\"."
                : $"ffmpeg was not found; it is needed to stitch. {FfmpegLocator.InstallHint} Then restart this app, or use \"Browse for ffmpeg...\".";
    }

    /// <summary>The stitch settings as chosen in the window.</summary>
    internal StitchOptions BuildStitchOptions() => new()
    {
        Fov = (double)(StitchFov ?? 195),
        Yaw = (double)(StitchYaw ?? 0),
        Pitch = (double)(StitchPitch ?? 0),
        Roll = (double)(StitchRoll ?? 0),
        Codec = StitchCodec,
        Crf = (int)(StitchCrf ?? 20),
        Preset = StitchPreset,
        Interpolation = StitchInterpolation,
        HighQualityEncoderTuning = StitchHighQualityTuning,
        Audio = StitchAudio,
        Encoder = StitchEncoder,
    };

    /// <summary>Puts the quality settings a profile covers into the Advanced controls.</summary>
    private void ShowQualitySettings(StitchOptions options)
    {
        StitchCodec = options.Codec;
        StitchCrf = options.Crf;
        StitchPreset = options.Preset;
        StitchInterpolation = options.Interpolation;
        StitchHighQualityTuning = options.HighQualityEncoderTuning;
    }

    partial void OnStitchProfileChanged(StitchProfile value)
    {
        if (!_ready || _applyingProfile || value == StitchProfile.Custom)
        {
            return;
        }

        _applyingProfile = true;
        try
        {
            ShowQualitySettings(StitchProfiles.ApplyTo(BuildStitchOptions(), value));
        }
        finally
        {
            _applyingProfile = false;
        }
    }

    partial void OnStitchCodecChanged(StitchCodec value) => OnQualitySettingEdited();

    partial void OnStitchCrfChanged(decimal? value) => OnQualitySettingEdited();

    partial void OnStitchPresetChanged(string value) => OnQualitySettingEdited();

    partial void OnStitchInterpolationChanged(StitchInterpolation value) => OnQualitySettingEdited();

    partial void OnStitchHighQualityTuningChanged(bool value) => OnQualitySettingEdited();

    private void OnQualitySettingEdited()
    {
        if (!_ready || _applyingProfile)
        {
            return;
        }

        _applyingProfile = true;
        try
        {
            StitchProfile = StitchProfiles.Match(BuildStitchOptions());
        }
        finally
        {
            _applyingProfile = false;
        }
    }

    partial void OnStitchVideosChanged(bool value)
    {
        // Ticking "Stitch" without ffmpeg offers the download straight away (the user still has to confirm it).
        if (value && _ready && !IsBusy && !IsFfmpegFound && _stitch.DownloadPackage is not null)
        {
            FfmpegOfferTask = OfferFfmpegDownloadAsync();
        }
    }

    private bool CanDownloadFfmpeg() => !IsBusy && !IsFfmpegFound && _stitch.DownloadPackage is not null;

    /// <summary>Downloads ffmpeg from its publisher (the "Download ffmpeg" button).</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadFfmpeg))]
    private Task DownloadFfmpegAsync() => DownloadFfmpegCoreAsync();

    /// <summary>
    /// Looks for ffmpeg again and, when it is still missing, asks whether to download it and does so on yes.
    /// Returns the tools, or null when there are none.
    /// </summary>
    private async Task<FfmpegTools?> OfferFfmpegDownloadAsync()
    {
        var tools = _stitch.TryLocate(FfmpegPath);
        UpdateFfmpegStatus(tools);
        if (tools is not null || _stitch.DownloadPackage is not { } package)
        {
            return tools;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "Download ffmpeg?",
            $"ffmpeg is needed to stitch 360 video and was not found on this computer. Download ffmpeg {package.Version} " +
            $"(about {ByteSize.Format(package.TotalSize)}) now?\n\n{package.LicenseNote}",
            "Download ffmpeg").ConfigureAwait(true);
        return confirmed ? await DownloadFfmpegCoreAsync().ConfigureAwait(true) : null;
    }

    private async Task<FfmpegTools?> DownloadFfmpegCoreAsync()
    {
        if (IsBusy || _stitch.DownloadPackage is not { } package)
        {
            return null;
        }

        using var cts = new CancellationTokenSource();
        _copyCts = cts;
        IsBusy = true;
        IsDownloadingFfmpeg = true;
        Progress = 0;
        ProgressText = "Downloading ffmpeg...";
        AppendLog($"Downloading ffmpeg {package.Version} ({ByteSize.Format(package.TotalSize)}) from {package.Publisher}...");
        try
        {
            var progress = new CallbackProgress<(long Done, long? Total)>(p =>
            {
                _latestDownload = p;
                PostDrain();
            });
            var tools = await Task.Run(() => _stitch.InstallFfmpegAsync(progress, cts.Token), cts.Token).ConfigureAwait(true);
            _latestDownload = null;
            DrainPending();

            // The downloaded copy is found by the normal search, so forget any stale location picked earlier.
            FfmpegPath = null;
            UpdateFfmpegStatus(tools);
            SaveSettings();
            AppendLog($"ffmpeg installed in {Path.GetDirectoryName(tools.FfmpegPath)} (SHA-256 checked). {package.LicenseNote}");
            Progress = 100;
            ProgressText = "ffmpeg downloaded.";
            return tools;
        }
        catch (OperationCanceledException)
        {
            AppendLog("ffmpeg download cancelled; nothing was installed.");
            ProgressText = "Cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            AppendLog("ffmpeg download failed: " + ex.Message);
            ProgressText = "ffmpeg download failed; see the log.";
            return null;
        }
        finally
        {
            _latestDownload = null;
            _copyCts = null;
            IsDownloadingFfmpeg = false;
            IsBusy = false;
        }
    }

    private bool CanCopy() =>
        !IsBusy && SelectedSource is not null && !string.IsNullOrWhiteSpace(Destination) && Media.Any(m => m.IsSelected);

    /// <summary>Copies the ticked files, then stitches the videos when asked to.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyAsync()
    {
        var source = SelectedSource?.Source;
        var files = Media.Where(m => m.IsSelected).Select(m => m.File).ToList();
        if (source is null || files.Count == 0)
        {
            return;
        }

        var destination = Destination.Trim();
        if (!Path.IsPathFullyQualified(destination))
        {
            AppendLog($"The destination must be a full folder path: {destination}");
            return;
        }

        StitchOptions? stitchOptions = null;
        FfmpegTools? tools = null;
        if (StitchVideos)
        {
            tools = await OfferFfmpegDownloadAsync().ConfigureAwait(true);
            if (tools is null)
            {
                AppendLog("Cannot stitch: " + FfmpegStatus);
                return;
            }

            stitchOptions = BuildStitchOptions();
            try
            {
                stitchOptions.Validate();
            }
            catch (ArgumentOutOfRangeException ex)
            {
                AppendLog("Stitch settings: " + ex.Message);
                return;
            }

            // Checked before copying, so an encoder that does not work here is reported straight away.
            try
            {
                var options = stitchOptions;
                var encoder = await Task.Run(() => _stitch.ResolveEncoderAsync(tools, options, CancellationToken.None)).ConfigureAwait(true);
                AppendLog($"Stitch encoder: {encoder.DisplayName()} ({encoder.FfmpegName(stitchOptions.Codec)})" +
                    (StitchEncoder == StitchEncoder.Auto && encoder == StitchEncoder.Cpu ? ", no working graphics card encoder found." : "."));
                AppendLog($"Stitch quality {StitchProfile}: {StitchProfiles.Summarize(stitchOptions, encoder)}");
            }
            catch (StitchException ex)
            {
                AppendLog("Cannot stitch: " + ex.Message);
                return;
            }
        }

        if (DeleteAfterCopy)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "Delete from camera?",
                $"Each of the {files.Count} selected files will be deleted from {source.DisplayName} after it has been copied " +
                "and its size checked. This cannot be undone.",
                "Copy and delete").ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }
        }

        SaveSettings();
        using var cts = new CancellationTokenSource();
        _copyCts = cts;
        LastCopyDestination = null;
        IsBusy = true;
        Progress = 0;
        ImportResult? result = null;
        try
        {
            AppendLog($"Copying {files.Count} file{(files.Count == 1 ? string.Empty : "s")} from {source.DisplayName} to {destination}");
            _bytesBefore = new long[files.Count];
            for (var i = 1; i < files.Count; i++)
            {
                _bytesBefore[i] = _bytesBefore[i - 1] + files[i - 1].Size;
            }

            _totalBytes = files.Sum(f => f.Size);
            var options = new ImportOptions { Destination = destination, DeleteAfter = DeleteAfterCopy };
            var progress = new CallbackProgress<ImportProgress>(OnImportProgress);
            result = await Task.Run(() => _importService.ImportFilesAsync(source, files, options, progress, cts.Token), cts.Token).ConfigureAwait(true);
            DrainPending();
            _latestImport = null;

            AppendLog(
                $"Copy done: {result.CopiedPaths.Count} copied, {result.SkippedPaths.Count} already there, {result.Failures.Count} failed" +
                (options.DeleteAfter ? $", {result.DeletedCount} deleted from the camera." : "."));
            Progress = 100;
            ProgressText = result.Succeeded ? "Copy finished." : "Copy finished with errors; see the log.";
            LastCopyDestination = destination;

            if (tools is not null && stitchOptions is not null)
            {
                await StitchAllAsync(tools, stitchOptions, result, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            DrainPending();
            AppendLog("Cancelled. Files already finished were kept; the partial file was removed.");
            ProgressText = "Cancelled.";
            LastCopyDestination = Directory.Exists(destination) ? destination : null;
        }
        catch (Exception ex)
        {
            DrainPending();
            AppendLog($"Error: {ex.Message}");
            ProgressText = "Failed; see the log.";
        }
        finally
        {
            _latestImport = null;
            _latestStitchFraction = null;
            _copyCts = null;
            IsBusy = false;
        }

        if (result is { DeletedCount: > 0 } && !_disposed && ReferenceEquals(SelectedSource?.Source, source))
        {
            // Show what is left on the camera.
            OnSelectedSourceChanged(SelectedSource);
        }
    }

    private async Task StitchAllAsync(FfmpegTools tools, StitchOptions options, ImportResult result, CancellationToken cancellationToken)
    {
        // Already-present files are included so an interrupted stitch can be resumed; existing _stitched files are skipped.
        var videos = result.CopiedPaths.Concat(result.SkippedPaths)
            .Where(p => MediaKinds.FromPath(p) == MediaKind.Video && !Stitcher.IsStitchedOutput(p))
            .ToList();
        if (videos.Count == 0)
        {
            AppendLog("No videos to stitch.");
            return;
        }

        int stitched = 0, skipped = 0, failed = 0;
        for (var i = 0; i < videos.Count; i++)
        {
            var video = videos[i];
            _stitchPrefix = string.Create(CultureInfo.CurrentCulture, $"Stitching {i + 1}/{videos.Count}: {Path.GetFileName(video)}");
            ProgressText = _stitchPrefix;
            Progress = 100.0 * i / videos.Count;
            var index = i;
            var count = videos.Count;
            var progress = new CallbackProgress<double>(fraction => OnStitchProgress((index + Math.Clamp(fraction, 0, 1)) / count));
            try
            {
                var stitchResult = await Task.Run(() => _stitch.StitchAsync(tools, video, options, progress, cancellationToken), cancellationToken).ConfigureAwait(true);
                if (stitchResult.Skipped)
                {
                    skipped++;
                    AppendLog($"Stitch skipped {Path.GetFileName(video)}: {stitchResult.OutputPath} already exists.");
                }
                else
                {
                    stitched++;
                    var with = stitchResult.Encoder is { } used ? $" with {used.DisplayName()}" : string.Empty;
                    var fallback = stitchResult.FallbackReason is { } reason ? $" ({reason}, so the CPU was used)" : string.Empty;
                    var tuning = stitchResult.TuningSkipped ? " (HQ tuning is not supported by this graphics card; standard settings used)" : string.Empty;
                    AppendLog($"Stitched {Path.GetFileName(video)}{with}{fallback}{tuning} -> {stitchResult.OutputPath}");
                }
            }
            catch (Exception ex) when (ex is StitchException or IOException or UnauthorizedAccessException)
            {
                failed++;
                AppendLog($"Stitch FAILED for {Path.GetFileName(video)}: {ex.Message}");
            }
            finally
            {
                _latestStitchFraction = null;
            }
        }

        Progress = 100;
        AppendLog($"Stitching done: {stitched} stitched, {skipped} skipped, {failed} failed.");
        ProgressText = failed == 0 ? "All done." : "Done with errors; see the log.";
    }

    /// <summary>Stops the running copy or stitch.</summary>
    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _copyCts?.Cancel();
        ProgressText = "Cancelling...";
    }

    private bool CanOpenDestination() => !IsBusy && LastCopyDestination is not null;

    /// <summary>Opens the last destination in the file manager.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenDestination))]
    private async Task OpenDestinationAsync()
    {
        if (LastCopyDestination is { } folder)
        {
            try
            {
                await _dialogs.OpenFolderAsync(folder).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppendLog($"Could not open {folder}: {ex.Message}");
            }
        }
    }

    // Runs on the copying thread.
    private void OnImportProgress(ImportProgress report)
    {
        switch (report.Status)
        {
            case ImportFileStatus.Copied:
                _pendingLog.Enqueue($"[{report.Index}/{report.Total}] Copied {report.File.Name}");
                break;
            case ImportFileStatus.Skipped:
                _pendingLog.Enqueue($"[{report.Index}/{report.Total}] Already there: {report.File.Name}");
                break;
            case ImportFileStatus.Failed:
                _pendingLog.Enqueue($"[{report.Index}/{report.Total}] FAILED {report.File.Name}: {report.Error}");
                break;
        }

        _latestImport = report;
        PostDrain();
    }

    // Runs on the stitching thread.
    private void OnStitchProgress(double overall)
    {
        _latestStitchFraction = overall;
        PostDrain();
    }

    private void PostDrain()
    {
        // At most one drain is queued at a time, so fast progress does not flood the UI thread.
        if (Interlocked.Exchange(ref _drainPosted, 1) == 0)
        {
            _dispatcher.Post(() =>
            {
                Volatile.Write(ref _drainPosted, 0);
                DrainPending();
            });
        }
    }

    /// <summary>Applies queued log lines and the latest progress. UI thread only.</summary>
    private void DrainPending()
    {
        while (_pendingLog.TryDequeue(out var line))
        {
            AppendLog(line);
        }

        if (!IsBusy)
        {
            return;
        }

        if (_latestImport is { } report && report.Index - 1 < _bytesBefore.Length)
        {
            var done = _bytesBefore[report.Index - 1] + report.BytesCopied;
            Progress = _totalBytes > 0 ? Math.Clamp(100.0 * done / _totalBytes, 0, 100) : 100.0 * report.Index / report.Total;
            ProgressText = string.Create(
                CultureInfo.CurrentCulture,
                $"Copying {report.Index}/{report.Total}: {report.File.Name} ({ByteSize.Format(report.BytesCopied)} of {ByteSize.Format(report.FileSize)})");
        }

        if (_latestDownload is { Total: > 0 } download)
        {
            Progress = Math.Clamp(100.0 * download.Done / download.Total.Value, 0, 100);
            ProgressText = string.Create(
                CultureInfo.CurrentCulture,
                $"Downloading ffmpeg: {ByteSize.Format(download.Done)} of {ByteSize.Format(download.Total.Value)}");
        }

        if (_latestStitchFraction is { } fraction)
        {
            Progress = Math.Clamp(fraction * 100, 0, 100);
            ProgressText = string.Create(CultureInfo.CurrentCulture, $"{_stitchPrefix} ({fraction * 100:0}% overall)");
        }
    }

    private void AppendLog(string line)
    {
        Log.Add(string.Create(CultureInfo.CurrentCulture, $"{DateTime.Now:HH:mm:ss}  {line}"));
        while (Log.Count > MaxLogLines)
        {
            Log.RemoveAt(0);
        }
    }

    private void SaveSettings() =>
        _settingsStore.Save(new AppSettings
        {
            LastDestination = string.IsNullOrWhiteSpace(Destination) ? null : Destination.Trim(),
            FfmpegPath = FfmpegPath,
            Stitch = StitchVideos,
            StitchFov = (double)(StitchFov ?? 195),
            StitchCodec = StitchCodec,
            StitchCrf = (int)(StitchCrf ?? 20),
            StitchEncoder = StitchEncoder,
            StitchProfile = StitchProfile,
            StitchPreset = StitchPreset,
            StitchInterpolation = StitchInterpolation,
            StitchHighQualityTuning = StitchHighQualityTuning,
            StitchAudio = StitchAudio,
            StitchYaw = (double)(StitchYaw ?? 0),
            StitchPitch = (double)(StitchPitch ?? 0),
            StitchRoll = (double)(StitchRoll ?? 0),
        });

    private static async Task IgnoreFailure(Task task)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Only waiting for it to finish; its own handler reported the problem.
        }
    }

    private static async Task DisposeQuietlyAsync(ICameraSource source)
    {
        try
        {
            await source.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // A camera that was unplugged may fail to close; there is nothing else to do with it.
        }
    }

    /// <summary>Calls the handler on the reporting thread (unlike <see cref="Progress{T}"/>, which posts).</summary>
    private sealed class CallbackProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
