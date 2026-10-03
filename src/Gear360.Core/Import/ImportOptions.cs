namespace Gear360.Core.Import;

/// <summary>Settings for <see cref="ImportService"/>.</summary>
public sealed record ImportOptions
{
    /// <summary>Folder that receives the files. Each file goes to <c>&lt;Destination&gt;/&lt;yyyy-MM-dd&gt;/&lt;name&gt;</c>.</summary>
    public required string Destination { get; init; }

    /// <summary>Import videos only and ignore photos.</summary>
    public bool VideosOnly { get; init; }

    /// <summary>Import only files modified at or after this time. Files with no date are left out when this is set.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Delete each file from the source after it has been copied and its size verified.</summary>
    public bool DeleteAfter { get; init; }

    /// <summary>Replace an existing destination file whose size differs, instead of writing a numbered copy next to it.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Returns true when <paramref name="file"/> passes the <see cref="VideosOnly"/> and <see cref="Since"/> filters.</summary>
    public bool Includes(CameraFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (VideosOnly && file.Kind != MediaKind.Video)
        {
            return false;
        }

        if (Since is { } since && (file.Modified is not { } modified || modified < since))
        {
            return false;
        }

        return true;
    }
}
