namespace Gear360.Core;

/// <summary>A file stored on a camera (or on its memory card).</summary>
/// <param name="Id">Backend-specific identifier used to read or delete the file.</param>
/// <param name="RelativePath">Path from the storage root with forward slashes, e.g. <c>DCIM/100PHOTO/SAM_0001.MP4</c>.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="Modified">Last-modified (or capture) time, when the device reports one.</param>
public sealed record CameraFile(string Id, string RelativePath, long Size, DateTimeOffset? Modified)
{
    /// <summary>The file name without its folders.</summary>
    public string Name => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];

    /// <summary>The media kind, decided by the file extension.</summary>
    public MediaKind Kind => MediaKinds.FromPath(RelativePath);
}
