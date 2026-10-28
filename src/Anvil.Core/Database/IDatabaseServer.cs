using Anvil.Core.Processes;

namespace Anvil.Core.Database;

/// <summary>The database server as the Agent drives it: the real one is <see cref="DatabaseServer"/>.</summary>
public interface IDatabaseServer
{
    event EventHandler<ProcessStatus>? StatusChanged;

    ProcessStatus? Status(DbEngine e);

    /// <summary>True when the engine's datadir already holds a database.</summary>
    bool Initialized(DbEngine e);

    Task InitializeAsync(DbEngine e, string version, string rootPassword, CancellationToken ct);

    /// <summary>Starts the server and returns once it accepts connections; throws with the reason when it does not.</summary>
    Task StartAsync(DbEngine e, string version, CancellationToken ct);

    Task StopAsync(DbEngine e, string version, CancellationToken ct);

    /// <summary>Stores the password when it logs in as root to the running server; false when it does not.</summary>
    Task<bool> AdoptRootPasswordAsync(DbEngine e, string password, CancellationToken ct);

    Task SetRootPasswordAsync(DbEngine e, string password, CancellationToken ct);

    /// <summary>The installed server's built-in defaults.</summary>
    Task<IReadOnlyDictionary<string, string>> DefaultsAsync(DbEngine e, string version, CancellationToken ct);

    /// <summary>Dumps every database; returns the .sql.gz path.</summary>
    Task<string> BackupAsync(DbEngine e, string version, CancellationToken ct);
}
