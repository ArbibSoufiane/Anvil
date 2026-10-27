using ArbiPache.Core.Database;

namespace ArbiPache.Core.Settings;

public enum ServerMode { Development, Production }

public sealed record ApacheTuning(int ThreadsPerChild, int MaxConnectionsPerChild, int KeepAliveTimeout);

public sealed record PhpPoolSettings(int Workers, int MaxRequests);

public sealed record NetworkAccess(bool HtdocsOnNetwork, bool PrivateNetworksOnly);

/// <summary>Contents of data\arbipache.json.</summary>
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public int HttpPort { get; init; } = 80;
    public int HttpsPort { get; init; } = 443;
    public ServerMode Mode { get; init; } = ServerMode.Development;
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
    public NetworkAccess Network { get; init; } = new(true, true);
    public bool StartServicesOnLaunch { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public bool CloseToTray { get; init; } = true;
    public bool RunAsService { get; init; }
    public string Editor { get; init; } = "auto";
    public string? HtdocsPath { get; init; }
    public string? AcmeEmail { get; init; }
    public bool AcmeStaging { get; init; }
    public IReadOnlyList<string> ControlUserSids { get; init; } = [];
}
