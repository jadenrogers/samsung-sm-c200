using System.Globalization;

using Gear360.Core;

namespace Gear360.Mtp.LibMtp;

/// <summary>
/// Walks the <c>DCIM</c> folder of each MTP storage and collects the photos and videos in it.
/// The folder listing is passed in, so the walk is independent of libmtp and can be unit-tested.
/// </summary>
internal static class MediaTreeWalker
{
    /// <summary>Deepest folder level searched below the storage root; guards against odd devices.</summary>
    public const int MaxDepth = 16;

    /// <summary>The folder cameras keep their media in.</summary>
    public const string DcimFolderName = "DCIM";

    /// <summary>Delegate that lists the children of <c>parentId</c> on <c>storageId</c>.</summary>
    public delegate IReadOnlyList<MtpObject> ListChildren(uint storageId, uint parentId);

    /// <summary>Returns the <c>DCIM</c> folder at the root of <paramref name="storageId"/>, or null.</summary>
    public static MtpObject? FindDcim(uint storageId, uint rootParentId, ListChildren list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list(storageId, rootParentId)
            .FirstOrDefault(o => o.IsFolder && o.Name.Equals(DcimFolderName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the media files under <c>DCIM</c> on each storage, sorted by path. <see cref="CameraFile.Id"/>
    /// is the MTP object handle in decimal; <see cref="CameraFile.RelativePath"/> starts with <c>DCIM/</c>.
    /// </summary>
    public static IReadOnlyList<CameraFile> FindMedia(IEnumerable<uint> storageIds, uint rootParentId, ListChildren list, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storageIds);
        ArgumentNullException.ThrowIfNull(list);

        var files = new List<CameraFile>();
        var visited = new HashSet<uint>();
        foreach (var storageId in storageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FindDcim(storageId, rootParentId, list) is { } dcim && visited.Add(dcim.ItemId))
            {
                Walk(storageId, dcim.ItemId, dcim.Name, depth: 1, list, visited, files, cancellationToken);
            }
        }

        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return files;
    }

    private static void Walk(
        uint storageId,
        uint folderId,
        string folderPath,
        int depth,
        ListChildren list,
        HashSet<uint> visited,
        List<CameraFile> files,
        CancellationToken cancellationToken)
    {
        foreach (var child in list(storageId, folderId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // An object can only appear once; this also stops loops on devices that report bad parents.
            if (!visited.Add(child.ItemId))
            {
                continue;
            }

            var path = $"{folderPath}/{SanitizeName(child.Name)}";
            if (child.IsFolder)
            {
                if (depth < MaxDepth)
                {
                    Walk(storageId, child.ItemId, path, depth + 1, list, visited, files, cancellationToken);
                }
            }
            else if (MediaKinds.FromPath(child.Name) != MediaKind.Other)
            {
                files.Add(new CameraFile(child.ItemId.ToString(CultureInfo.InvariantCulture), path, child.Size, child.Modified));
            }
        }
    }

    /// <summary>Keeps a device-supplied name from adding path levels of its own.</summary>
    private static string SanitizeName(string name)
    {
        var clean = name.Replace('/', '_').Replace('\\', '_');
        return clean is "" or "." or ".." ? "_" : clean;
    }
}
