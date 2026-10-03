using System.Text.Json;
using System.Text.Json.Serialization;
using Gear360.Core.Stitching;

namespace Gear360.Gui.Services;

/// <summary>What the app remembers between runs.</summary>
public sealed record AppSettings
{
    /// <summary>The last destination folder used for a copy.</summary>
    public string? LastDestination { get; init; }

    /// <summary>An ffmpeg location chosen by the user; null to search PATH.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>Whether "Stitch videos to 360°" was ticked.</summary>
    public bool Stitch { get; init; }

    /// <summary>
    /// The stitch quality profile. Null in settings saved before profiles existed; the profile is then worked out from
    /// the saved codec and CRF.
    /// </summary>
    public StitchProfile? StitchProfile { get; init; }

    /// <summary>The lens field of view used for stitching.</summary>
    public double StitchFov { get; init; } = 195;

    /// <summary>The stitch codec.</summary>
    public StitchCodec StitchCodec { get; init; } = StitchCodec.H264;

    /// <summary>The stitch quality (CRF).</summary>
    public int StitchCrf { get; init; } = 20;

    /// <summary>Who encodes stitched video (Auto: the graphics card when it can).</summary>
    public StitchEncoder StitchEncoder { get; init; } = StitchEncoder.Auto;

    /// <summary>The x264/x265 preset name.</summary>
    public string StitchPreset { get; init; } = "medium";

    /// <summary>How v360 samples the fisheye pictures.</summary>
    public StitchInterpolation StitchInterpolation { get; init; } = StitchInterpolation.Linear;

    /// <summary>Use the hardware encoder's best settings.</summary>
    public bool StitchHighQualityTuning { get; init; }

    /// <summary>Copy or re-encode the sound.</summary>
    public StitchAudio StitchAudio { get; init; } = StitchAudio.Copy;

    /// <summary>Yaw of the stitched view, in degrees.</summary>
    public double StitchYaw { get; init; }

    /// <summary>Pitch of the stitched view, in degrees.</summary>
    public double StitchPitch { get; init; }

    /// <summary>Roll of the stitched view, in degrees.</summary>
    public double StitchRoll { get; init; }
}

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public interface ISettingsStore
{
    /// <summary>Returns the saved settings, or defaults when there are none.</summary>
    AppSettings Load();

    /// <summary>Saves the settings; failures are ignored.</summary>
    void Save(AppSettings settings);
}

/// <summary>Keeps settings in a small JSON file, by default in the user's application data folder.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    /// <summary>Creates a store backed by <paramref name="path"/>.</summary>
    public JsonSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = path;
    }

    /// <summary>The settings file.</summary>
    public string FilePath { get; }

    /// <summary>The default location: <c>&lt;app data&gt;/Gear360Extractor/settings.json</c>.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gear360Extractor", "settings.json");

    /// <inheritdoc />
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or damaged settings file just means defaults.
            return new AppSettings();
        }
    }

    /// <inheritdoc />
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; never fail an operation because they could not be written.
        }
    }
}

/// <summary>Source-generated JSON metadata for <see cref="AppSettings"/>.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
