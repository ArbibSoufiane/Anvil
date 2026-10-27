namespace ArbiPache.Core.Components;

/// <summary>Lists releases of one component from an online source.</summary>
public interface ICatalogProvider
{
    ComponentKind Kind { get; }

    Task<IReadOnlyList<ComponentRelease>> GetAsync(CancellationToken ct);
}
