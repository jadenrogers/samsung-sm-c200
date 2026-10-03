namespace Gear360.Mtp.Windows;

/// <summary>Turns device paths such as <c>\Card\DCIM\100PHOTO\SAM_0001.MP4</c> into storage-relative paths.</summary>
internal static class WpdPaths
{
    /// <summary>
    /// Returns the path of <paramref name="fileFullName"/> relative to <paramref name="storageRoot"/>,
    /// with forward slashes (e.g. <c>DCIM/100PHOTO/SAM_0001.MP4</c>). When the file is not under the
    /// root, the path from its DCIM folder is used, or just the file name.
    /// </summary>
    public static string ToRelativePath(string storageRoot, string fileFullName)
    {
        ArgumentNullException.ThrowIfNull(storageRoot);
        ArgumentNullException.ThrowIfNull(fileFullName);

        var root = Normalize(storageRoot);
        var file = Normalize(fileFullName);

        if (root.Length == 0)
        {
            return file;
        }

        if (file.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return file[(root.Length + 1)..];
        }

        if (file.StartsWith("DCIM/", StringComparison.OrdinalIgnoreCase))
        {
            return file;
        }

        var dcim = file.IndexOf("/DCIM/", StringComparison.OrdinalIgnoreCase);
        if (dcim >= 0)
        {
            return file[(dcim + 1)..];
        }

        return file[(file.LastIndexOf('/') + 1)..];
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}
