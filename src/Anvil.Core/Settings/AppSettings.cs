using Anvil.Core.Database;

namespace Anvil.Core.Settings;

public sealed record ApacheTuning(int ThreadsPerChild, int MaxConnectionsPerChild, int KeepAliveTimeout);

public sealed record PhpPoolSettings(int Workers, int MaxRequests);

/// <param name="HtdocsOnNetwork">htdocs and its entries may be opened from the local network, not only this PC.</param>
public sealed record NetworkAccess(bool HtdocsOnNetwork);

/// <summary>Contents of data\anvil.json.</summary>
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public int HttpPort { get; init; } = 80;
    public int HttpsPort { get; init; } = 443;
    public string? ActiveApache { get; init; }
    public string? DefaultPhp { get; init; }
    public DbEngine? DbEngine { get; init; }
    public string? DbVersion { get; init; }
    public int DbPort { get; init; } = 3306;
    public ApacheTuning Apache { get; init; } = new(Suggest.Threads(Environment.ProcessorCount), 0, 5);
    public IReadOnlyList<string> OptionalModules { get; init; } = ["deflate", "expires", "http2"];
    /// <summary>Keyed by PHP version, e.g. "8.5.5".</summary>
    public IReadOnlyDictionary<string, PhpPoolSettings> PhpPools { get; init; } = new Dictionary<string, PhpPoolSettings>();
    /// <summary>Persisted pool ports, keyed by pool key ("php85").</summary>
    public IReadOnlyDictionary<string, int> PhpPoolPorts { get; init; } = new Dictionary<string, int>();
    public NetworkAccess Network { get; init; } = new(true);
    /// <summary>The domain colleagues' PCs send to this PC's name service, e.g. "arbib.pc"; null when none is set.</summary>
    public string? NetworkDomain { get; init; }
    public bool StartServicesOnLaunch { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public bool CloseToTray { get; init; } = true;
    public string? HtdocsPath { get; init; }
}
