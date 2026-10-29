using Anvil.Core.Processes;

namespace Anvil.Core.Database;

/// <summary>
/// Runs the servers the user linked: through their Windows service, or, without one, under the Agent like its own
/// database.
/// </summary>
public interface ILinkedDbHost
{
    /// <exception cref="ArgumentException">Not a server's bin folder; the message says why.</exception>
    DbFolderInfo Inspect(string binDir);

    ProcessState State(LinkedDatabase server);

    /// <summary>The user may start and stop it without an administrator prompt.</summary>
    bool MayControl(LinkedDatabase server);

    Task StartAsync(LinkedDatabase server, CancellationToken ct);

    /// <param name="password">For a clean shutdown of a server Anvil runs; a service stops through Windows.</param>
    Task StopAsync(LinkedDatabase server, string? password, CancellationToken ct);

    Task<bool> CanLogInAsync(LinkedDatabase server, string password, CancellationToken ct);

    /// <summary>Dumps every database to data\backups\db; returns the file.</summary>
    Task<string> BackupAsync(LinkedDatabase server, string password, CancellationToken ct);
}
