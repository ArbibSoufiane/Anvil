namespace Anvil.Core.Paths;

/// <summary>Every folder of the portable layout, resolved against one root.</summary>
public sealed class AppPaths(string root)
{
    public static AppPaths FromBaseDirectory() =>
        new(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));

    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public string Runtime => Path.Combine(Root, "runtime");
    public string Catalog => Path.Combine(Root, "catalog");
    public string Htdocs => Path.Combine(Root, "htdocs");
    public string Data => Path.Combine(Root, "data");
    public string Conf => Path.Combine(Data, "conf");
    public string Vhosts => Path.Combine(Conf, "vhosts");
    public string Sites => Path.Combine(Data, "sites");
    public string ApacheConfDir => Path.Combine(Data, "apache");
    public string HttpdConf => Path.Combine(ApacheConfDir, "httpd.conf");
    public string PhpConfRoot => Path.Combine(Data, "php");
    public string Certs => Path.Combine(Data, "certs");
    public string Logs => Path.Combine(Data, "logs");
    public string AgentLogs => Path.Combine(Logs, "anvil");

    /// <summary>The window's own log: with <see cref="ElevationRequests"/>, the only part of data\ the window writes.</summary>
    public string AppLogs => Path.Combine(Logs, "app");
    public string Backups => Path.Combine(Data, "backups");
    public string Run => Path.Combine(Data, "run");
    public string Tmp => Path.Combine(Data, "tmp");

    /// <summary>Where the window hands requests to Anvil.Elevator.exe, and the only folder the Elevator reads them from.</summary>
    public string ElevationRequests => Path.Combine(Tmp, "elevation");
    public string Secrets => Path.Combine(Data, "secrets");

    private string RootMarker => Path.Combine(Data, ".root");

    public string ApacheRuntime(string version) => Path.Combine(Runtime, "apache", version);
    public string PhpRuntime(string version) => Path.Combine(Runtime, "php", version);
    public string PhpConfigDir(string version) => Path.Combine(PhpConfRoot, version);

    public static string ForApache(string path) => path.Replace('\\', '/');

    /// <summary>True when data\.root is missing or names another folder (the root was moved or copied).</summary>
    public bool RootChanged()
    {
        if (!File.Exists(RootMarker)) return true;
        var recorded = File.ReadAllText(RootMarker).Trim();
        return !string.Equals(recorded, Root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The root the last successful start recorded, or null before the first one.</summary>
    public string? RecordedRoot() =>
        File.Exists(RootMarker) && File.ReadAllText(RootMarker).Trim() is { Length: > 0 } recorded ? recorded : null;

    /// <summary>
    /// The same place inside this root for a path inside <paramref name="oldRoot"/>, keeping the path's slashes; null for a
    /// path outside it.
    /// </summary>
    public string? Rebase(string path, string oldRoot)
    {
        var old = Path.TrimEndingDirectorySeparator(oldRoot.Replace('/', '\\'));
        var normalized = path.Replace('/', '\\');
        if (!normalized.StartsWith(old, StringComparison.OrdinalIgnoreCase)) return null;
        if (normalized.Length == old.Length) return Root;
        if (normalized[old.Length] != '\\') return null;
        var root = path.Contains('/', StringComparison.Ordinal) ? ForApache(Root) : Root;
        return root + path[old.Length..];
    }

    public void MarkRoot()
    {
        Directory.CreateDirectory(Data);
        File.WriteAllText(RootMarker, Root);
    }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Runtime, Catalog, Htdocs, Data, Conf, Vhosts, Sites, ApacheConfDir,
                     PhpConfRoot, Certs, Logs, Backups, Run, Tmp, Secrets })
            Directory.CreateDirectory(dir);
    }
}
