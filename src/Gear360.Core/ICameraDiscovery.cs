namespace Gear360.Core;

/// <summary>Finds cameras that are connected to this computer.</summary>
public interface ICameraDiscovery
{
    /// <summary>Returns the connected cameras. The caller owns (and disposes) the returned sources.</summary>
    Task<IReadOnlyList<ICameraSource>> DiscoverAsync(CancellationToken cancellationToken = default);
}
