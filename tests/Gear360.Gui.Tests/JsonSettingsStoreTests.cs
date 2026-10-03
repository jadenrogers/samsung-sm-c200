using Gear360.Core.Stitching;
using Gear360.Gui.Services;

namespace Gear360.Gui.Tests;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var store = new JsonSettingsStore(Path.Combine(_temp.Path, "none", "settings.json"));

        Assert.Equal(new AppSettings(), store.Load());
    }

    [Fact]
    public void Saved_settings_round_trip()
    {
        var store = new JsonSettingsStore(Path.Combine(_temp.Path, "app", "settings.json"));
        var settings = new AppSettings
        {
            LastDestination = "D:/Videos/Gear 360", FfmpegPath = "C:/tools/ffmpeg.exe", Stitch = true,
            StitchCodec = StitchCodec.Hevc, StitchCrf = 23, StitchFov = 193.5, StitchEncoder = StitchEncoder.Nvidia,
            StitchProfile = StitchProfile.Custom, StitchPreset = "slower", StitchInterpolation = StitchInterpolation.Lanczos,
            StitchHighQualityTuning = true, StitchAudio = StitchAudio.Aac, StitchYaw = 90, StitchPitch = -1.5, StitchRoll = 2,
        };

        store.Save(settings);

        Assert.Equal(settings, new JsonSettingsStore(store.FilePath).Load());
        Assert.Contains("\"Hevc\"", File.ReadAllText(store.FilePath), StringComparison.Ordinal);
        Assert.Contains("\"Nvidia\"", File.ReadAllText(store.FilePath), StringComparison.Ordinal);
        Assert.Contains("\"Lanczos\"", File.ReadAllText(store.FilePath), StringComparison.Ordinal);
        Assert.Contains("\"Custom\"", File.ReadAllText(store.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Damaged_file_gives_defaults()
    {
        var path = Path.Combine(_temp.Path, "settings.json");
        File.WriteAllText(path, "{ not json");

        Assert.Equal(new AppSettings(), new JsonSettingsStore(path).Load());
    }
}
