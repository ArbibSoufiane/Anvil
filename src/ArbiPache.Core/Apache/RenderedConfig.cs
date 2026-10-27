namespace ArbiPache.Core.Apache;

/// <param name="Files">File content keyed by path relative to data\conf, e.g. "vhosts\00-catchall.conf".</param>
public sealed record RenderedConfig(IReadOnlyDictionary<string, string> Files)
{
    /// <summary>Folders the configuration writes into, such as per-site log folders. They must exist before httpd -t.</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];
}
