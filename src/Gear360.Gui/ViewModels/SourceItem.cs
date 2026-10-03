using Gear360.Core;

namespace Gear360.Gui.ViewModels;

/// <summary>An entry of the source dropdown: a detected camera or a folder the user opened.</summary>
/// <param name="Source">The camera source; owned (and disposed) by the view model.</param>
/// <param name="IsFolder">True for a folder or SD card the user picked, false for a detected camera.</param>
public sealed record SourceItem(ICameraSource Source, bool IsFolder)
{
    /// <summary>The text shown in the dropdown.</summary>
    public string DisplayName => Source.DisplayName;
}
