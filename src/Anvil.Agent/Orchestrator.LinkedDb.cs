using Anvil.Core.Agent;
using Anvil.Core.Database;
using Anvil.Core.Elevation;
using Anvil.Core.Processes;

namespace Anvil.Agent;

/// <summary>
/// MariaDB and MySQL servers the user installed themselves. A server run by a Windows service keeps running through it:
/// Windows asks once, when it is linked, to let the user start and stop that service, and each time its port changes,
/// since its settings file sits in a protected folder. A server with no service runs under the Agent on the port Anvil
/// keeps for it.
/// </summary>
public sealed partial class Orchestrator
{
    private const string NoLinkedHost = "Linked databases are not available in this Agent.";

    private LinkedDatabaseStore Linked => new(paths);

    private static string DbName(DbEngine e) => e == DbEngine.MariaDb ? "MariaDB" : "MySQL";

    private static string Title(LinkedDatabase d) => $"{DbName(d.Engine)} {d.Version}";

    private static string Title(DbFolderInfo d) => $"{DbName(d.Engine)} {d.Version}";

    public IReadOnlyList<LinkedDbInfo> GetLinkedDatabases() =>
        linkedDbs is null ? [] : Linked.All().Select(d => new LinkedDbInfo(d.Id, d.Engine, d.Version, d.BinDir, d.Service, d.Port, d.User,
            linkedDbs.State(d), linkedDbs.MayControl(d))).ToList();

    /// <summary>
    /// Keeps the server in the bin folder with the account Anvil logs in with. A running server must accept it. A service's
    /// result asks Windows to let the user start and stop it.
    /// </summary>
    public Task<ApplyResult> LinkDatabaseAsync(string binDir, string user, string password, CancellationToken ct) => Locked(async () =>
    {
        if (linkedDbs is null) return ApplyResult.Failure(NoLinkedHost);
        if (string.IsNullOrWhiteSpace(user)) return ApplyResult.Failure("Enter the user Anvil logs in with, such as root.");
        DbFolderInfo found;
        try
        {
            found = linkedDbs.Inspect(binDir);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return ApplyResult.Failure(e.Message);
        }

        var all = Linked.All();
        if (all.Any(d => string.Equals(d.BinDir, found.BinDir, StringComparison.OrdinalIgnoreCase)))
            return ApplyResult.Failure($"{Title(found)} in {found.BinDir} is linked already.");
        var server = new LinkedDatabase(LinkedDatabaseStore.IdFor(found, all.Select(d => d.Id).ToList()), found.Engine, found.Version, found.BinDir,
            found.Service, found.DefaultsFile, found.Port, user.Trim());
        if (linkedDbs.State(server) == ProcessState.Running)
        {
            try
            {
                if (!await linkedDbs.CanLogInAsync(server, password, ct))
                    return ApplyResult.Failure($"{server.User} could not log in to {Title(server)} on port {server.Port} with that password.");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return ApplyResult.Failure($"Anvil could not log in to {Title(server)} on port {server.Port}. {e.Message}");
            }
        }

        Linked.Save(server, password);
        return server.Service is { } service && !linkedDbs.MayControl(server)
            ? ApplyResult.Success() with { Elevation = new DbServiceAccessRequest(service, Grant: true) }
            : ApplyResult.Success();
    }, ct);

    /// <summary>Forgets the server and its password; a service's result takes back the user's right to start and stop it.</summary>
    public Task<ApplyResult> UnlinkDatabaseAsync(string id, CancellationToken ct) => Locked(async () =>
    {
        if (linkedDbs is null) return ApplyResult.Failure(NoLinkedHost);
        if (Find(id) is not { } server) return NotLinked(id);
        if (server.Service is null && linkedDbs.State(server) != ProcessState.Stopped)
            await linkedDbs.StopAsync(server, Linked.Password(id), ct);
        Linked.Remove(id);
        return server.Service is { } service
            ? ApplyResult.Success() with { Elevation = new DbServiceAccessRequest(service, Grant: false) }
            : ApplyResult.Success();
    }, ct);

    public Task<ApplyResult> StartLinkedDatabaseAsync(string id, CancellationToken ct) => Locked(() => ControlAsync(id, start: true, ct), ct);

    public Task<ApplyResult> StopLinkedDatabaseAsync(string id, CancellationToken ct) => Locked(() => ControlAsync(id, start: false, ct), ct);

    /// <summary>
    /// A service's port is written into the settings file it starts with, which needs Windows' permission: the first call
    /// returns that request, and the call after it finds the file changed and restarts a running server on the new port.
    /// </summary>
    public Task<ApplyResult> SetLinkedDatabasePortAsync(string id, int port, CancellationToken ct) => Locked(async () =>
    {
        if (linkedDbs is null) return ApplyResult.Failure(NoLinkedHost);
        if (Find(id) is not { } server) return NotLinked(id);
        if (port is < 1 or > 65535) return ApplyResult.Failure("The port must be between 1 and 65535.");
        if (port != server.Port && !portFree(port)) return ApplyResult.Failure($"Port {port} is already in use by another program.");

        if (server.Service is { } service)
        {
            if (server.DefaultsFile is not { } file)
                return ApplyResult.Failure($"{service} starts without a settings file, so Anvil cannot change its port.");
            if (DbPortFile.Read(await File.ReadAllTextAsync(file, ct)) != port)
                return ApplyResult.Failure($"Windows must allow Anvil to change {file}.") with { Elevation = new DbPortRequest(service, port) };
        }

        var moved = server with { Port = port };
        Linked.Save(moved);
        if (linkedDbs.State(server) != ProcessState.Running) return ApplyResult.Success();
        var restarted = await RestartLinkedAsync(server, moved, ct);
        return restarted.Ok ? ApplyResult.Success() : restarted;
    }, ct);

    public Task<string> BackupLinkedDatabaseAsync(string id, CancellationToken ct) => Locked(async () =>
    {
        if (linkedDbs is null) throw new InvalidOperationException(NoLinkedHost);
        var server = Find(id) ?? throw new InvalidOperationException($"No linked server is called {id}.");
        if (linkedDbs.State(server) != ProcessState.Running) throw new InvalidOperationException($"Start {Title(server)} first.");
        var password = Linked.Password(id) ?? throw new InvalidOperationException($"The password for {Title(server)} is not stored. Link it again.");
        return await linkedDbs.BackupAsync(server, password, ct);
    }, ct);

    /// <summary>A running server must let Anvil log in with the account it was linked with.</summary>
    public async Task<ApplyResult> CheckLinkedDatabaseAsync(string id, CancellationToken ct)
    {
        if (linkedDbs is null) return ApplyResult.Failure(NoLinkedHost);
        if (Find(id) is not { } server) return NotLinked(id);
        var state = linkedDbs.State(server);
        if (state != ProcessState.Running)
            return ApplyResult.Failure(state == ProcessState.Starting ? $"{Title(server)} is still starting." : $"{Title(server)} is not running.");
        if (Linked.Password(id) is not { } password)
            return ApplyResult.Failure($"{Title(server)} is running, but its password is not stored, so Anvil cannot log in. Link it again.");
        try
        {
            return await linkedDbs.CanLogInAsync(server, password, ct)
                ? ApplyResult.Success()
                : ApplyResult.Failure($"{Title(server)} is running, but {server.User} could not log in with the password Anvil keeps. Link it again.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ApplyResult.Failure($"{Title(server)} does not answer on port {server.Port}. {e.Message}");
        }
    }

    private async Task<ApplyResult> ControlAsync(string id, bool start, CancellationToken ct)
    {
        if (linkedDbs is null) return ApplyResult.Failure(NoLinkedHost);
        if (Find(id) is not { } server) return NotLinked(id);
        if (server.Service is { } service && !linkedDbs.MayControl(server))
            return ApplyResult.Failure($"Windows must allow you to start and stop {service} first.") with
            {
                Elevation = new DbServiceAccessRequest(service, Grant: true),
            };
        // Waiting for the port would find the other program's server and take it for this one.
        if (start && server.Service is null && linkedDbs.State(server) != ProcessState.Running && !portFree(server.Port))
            return ApplyResult.Failure($"Port {server.Port} is already in use by another program. Give {Title(server)} another port first.");
        try
        {
            if (start) await linkedDbs.StartAsync(server, ct);
            else await linkedDbs.StopAsync(server, Linked.Password(id), ct);
            return ApplyResult.Success();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ApplyResult.Failure($"{Title(server)} did not {(start ? "start" : "stop")}. {e.Message}");
        }
    }

    private async Task<ApplyResult> RestartLinkedAsync(LinkedDatabase before, LinkedDatabase after, CancellationToken ct)
    {
        try
        {
            await linkedDbs!.StopAsync(before, Linked.Password(before.Id), ct);
            await linkedDbs.StartAsync(after, ct);
            return ApplyResult.Success();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ApplyResult.Failure($"{Title(after)} did not start on port {after.Port}. {e.Message}");
        }
    }

    private LinkedDatabase? Find(string id) => Linked.All().FirstOrDefault(d => d.Id == id);

    private static ApplyResult NotLinked(string id) => ApplyResult.Failure($"No linked server is called {id}.");
}
