using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Gear360.Core;

namespace Gear360.Gui.ViewModels;

/// <summary>One row of the media list.</summary>
public sealed partial class MediaItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;

    /// <summary>Creates a row for <paramref name="file"/>; <paramref name="selectionChanged"/> runs whenever the tick changes.</summary>
    public MediaItemViewModel(CameraFile file, Action selectionChanged)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(selectionChanged);
        File = file;
        _selectionChanged = selectionChanged;
    }

    /// <summary>The camera file.</summary>
    public CameraFile File { get; }

    /// <summary>The file name.</summary>
    public string Name => File.Name;

    /// <summary>"Video", "Photo" or "Other".</summary>
    public string Kind => File.Kind.ToString();

    /// <summary>True for videos.</summary>
    public bool IsVideo => File.Kind == MediaKind.Video;

    /// <summary>The size, e.g. <c>1.2 GB</c>.</summary>
    public string SizeText => ByteSize.Format(File.Size);

    /// <summary>The local modified time, or a dash when the device reports none.</summary>
    public string DateText => File.Modified is { } modified
        ? modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
        : "-";

    /// <summary>Whether the file is ticked for copying.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}
