namespace Gear360.Core;

/// <summary>Combines several discovery backends and returns every camera any of them finds.</summary>
public sealed class CompositeCameraDiscovery : ICameraDiscovery
{
    private readonly IReadOnlyList<ICameraDiscovery> _backends;

    /// <summary>Creates a discovery that queries <paramref name="backends"/> in order.</summary>
    public CompositeCameraDiscovery(IEnumerable<ICameraDiscovery> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _backends = backends.ToArray();
    }

    /// <summary>The backends queried, in order.</summary>
    public IReadOnlyList<ICameraDiscovery> Backends => _backends;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ICameraSource>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var sources = new List<ICameraSource>();
        foreach (var backend in _backends)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sources.AddRange(await backend.DiscoverAsync(cancellationToken).ConfigureAwait(false));
        }

        return sources;
    }
}
