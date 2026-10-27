namespace ArbiPache.Core.Apache;

/// <param name="Files">File content keyed by path relative to data\conf, e.g. "vhosts\00-catchall.conf".</param>
public sealed record RenderedConfig(IReadOnlyDictionary<string, string> Files);
