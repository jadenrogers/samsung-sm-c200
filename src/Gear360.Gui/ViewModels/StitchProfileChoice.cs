using Gear360.Core.Stitching;

namespace Gear360.Gui.ViewModels;

/// <summary>An entry of the Quality dropdown: a profile with its one-line description.</summary>
/// <param name="Profile">The profile.</param>
public sealed record StitchProfileChoice(StitchProfile Profile)
{
    /// <summary>The profile's name, e.g. "High".</summary>
    public string Name => Profile.ToString();

    /// <summary>What the profile is for and what it costs.</summary>
    public string Description => StitchProfiles.Describe(Profile);
}
