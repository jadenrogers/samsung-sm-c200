namespace Gear360.Core;

/// <summary>Where the app keeps data it downloads (such as ffmpeg).</summary>
public static class AppDataPaths
{
    /// <summary>Environment variable that moves the data folder, e.g. for tests or portable use.</summary>
    public const string DataDirectoryVariable = "GEAR360_DATA_DIR";

    /// <summary>
    /// The data folder: <c>GEAR360_DATA_DIR</c> when set, otherwise <c>&lt;local app data&gt;/Gear360Extractor</c>
    /// (<c>%LOCALAPPDATA%</c> on Windows, <c>~/Library/Application Support</c> on macOS, <c>~/.local/share</c> on Linux).
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(DataDirectoryVariable);
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return Path.GetFullPath(overridePath.Trim());
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            }

            return Path.Combine(localAppData, "Gear360Extractor");
        }
    }
}
