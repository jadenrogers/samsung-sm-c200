namespace Gear360.Core;

/// <summary>The broad kind of a media file, decided by its extension.</summary>
public enum MediaKind
{
    /// <summary>Anything that is not a recognised photo or video.</summary>
    Other,

    /// <summary>A video clip (.mp4, .mov).</summary>
    Video,

    /// <summary>A still photo (.jpg, .jpeg).</summary>
    Photo,
}

/// <summary>Maps file names to <see cref="MediaKind"/>.</summary>
public static class MediaKinds
{
    /// <summary>Returns the media kind for a file name or path, by extension (case-insensitive).</summary>
    public static MediaKind FromPath(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mov", StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Video;
        }

        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Photo;
        }

        return MediaKind.Other;
    }
}
