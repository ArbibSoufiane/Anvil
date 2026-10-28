using System.Diagnostics;
using System.Net.Sockets;
using Anvil.Core.Paths;
using Anvil.Core.Processes;
using MySqlConnector;
using Serilog;

namespace Anvil.Core.Database;

/// <summary>Initializes, runs and stops the active MariaDB or MySQL server, and talks to it as root over 127.0.0.1.</summary>
public sealed class DatabaseManager(AppPaths paths, ProcessSupervisor sup, DbSecrets secrets, MyIni ini)
{
    private static readonly TimeSpan ToolLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReadyLimit = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(60);

    /// <summary>The supervisor's name for the server process.</summary>
    public static string ProcessName(DbEngine e) => e == DbEngine.MariaDb ? "mariadb" : "mysql";

    private string Bin(DbEngine e, string version) => Path.Combine(paths.DbRuntime(e, version), "bin");

    private static string ServerExe(DbEngine e) => e == DbEngine.MariaDb ? "mariadbd.exe" : "mysqld.exe";

    private static string AdminExe(DbEngine e) => e == DbEngine.MariaDb ? "mariadb-admin.exe" : "mysqladmin.exe";

    public int Port(DbEngine e) => int.Parse(ini.Read(e).GetValueOrDefault("port", "3306"), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Creates the system tables in an empty datadir and sets the root password (root@localhost only; no remote root).
    /// my.ini must exist (<see cref="MyIni.EnsureCreated"/>). The server is left stopped.
    /// </summary>
    public async Task InitializeAsync(DbEngine e, string version, string rootPassword, CancellationToken ct)
    {
        var data = ini.DataDir(e);
        if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any())
            throw new InvalidOperationException($"{data} already holds a database. Remove it to start over.");

        if (e == DbEngine.MariaDb)
        {
            await RunToolAsync(Path.Combine(Bin(e, version), "mariadb-install-db.exe"),
                [$"--datadir={data}", $"--password={rootPassword}", $"--port={Port(e)}"], ct);
            secrets.SaveRootPassword(rootPassword);
            return;
        }

        await RunToolAsync(Path.Combine(Bin(e, version), ServerExe(e)),
            [$"--defaults-file={ini.PathFor(e)}", "--initialize-insecure", $"--basedir={paths.DbRuntime(e, version)}"], ct);
        // --initialize-insecure leaves root@localhost without a password: set it before anything else can connect.
        sup.Start(Spec(e, version));
        try
        {
            await WaitUntilReadyAsync(e, ct);
            await using (var connection = await OpenAsync(e, "", ct))
            await using (var alter = new MySqlCommand("ALTER USER 'root'@'localhost' IDENTIFIED BY @pwd", connection))
            {
                alter.Parameters.AddWithValue("@pwd", rootPassword);
                await alter.ExecuteNonQueryAsync(ct);
            }
            secrets.SaveRootPassword(rootPassword);
        }
        finally
        {
            await StopAsync(e, version, ct);
        }
    }

    /// <summary>
    /// --defaults-file must come first, and keeps the server away from my.ini files elsewhere on the machine. No --console:
    /// it would send the error log to stdout and override log-error in my.ini.
    /// </summary>
    public ProcessSpec Spec(DbEngine e, string version) =>
        new(ProcessName(e), Path.Combine(Bin(e, version), ServerExe(e)), [$"--defaults-file={ini.PathFor(e)}"],
            new Dictionary<string, string>(), Bin(e, version), Path.Combine(paths.Logs, "db", $"{ProcessName(e)}.log"));

    /// <summary>
    /// Waits until the server accepts connections on its port. Fails at the first exit: a server that stops while
    /// starting stops again on every retry (a bad my.ini value), so the supervisor's restarts would only add delay.
    /// </summary>
    public async Task WaitUntilReadyAsync(DbEngine e, CancellationToken ct)
    {
        var port = Port(e);
        var deadline = Stopwatch.StartNew();
        var restarts = sup.Snapshot().FirstOrDefault(s => s.Name == ProcessName(e))?.Restarts ?? 0;
        while (deadline.Elapsed < ReadyLimit)
        {
            ct.ThrowIfCancellationRequested();
            var status = sup.Snapshot().FirstOrDefault(s => s.Name == ProcessName(e));
            if (status is { State: ProcessState.Failed or ProcessState.Stopped or ProcessState.Restarting } || status?.Restarts > restarts)
                throw new InvalidOperationException($"The database server stopped while starting. {status.LastError} See {ini.ErrorLog}.");
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync("127.0.0.1", port, ct);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(250, ct);
            }
        }
        throw new TimeoutException($"The database server did not accept connections on port {port} within {ReadyLimit.TotalSeconds:0} seconds.");
    }

    /// <summary>Asks the server to shut down cleanly (admin tool with a temporary option file), then stops supervising it.</summary>
    public Task StopAsync(DbEngine e, string version, CancellationToken ct)
    {
        var password = secrets.LoadRootPassword();
        return sup.StopAsync(ProcessName(e), password is null ? TimeSpan.FromSeconds(5) : ShutdownGrace,
            password is null ? null : () => AdminShutdownAsync(e, version, password, ct));
    }

    /// <summary>SHOW GLOBAL VARIABLES of the running server.</summary>
    public async Task<IReadOnlyDictionary<string, string>> RuntimeValuesAsync(DbEngine e, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(OptionNames.Comparer);
        await using var connection = await OpenAsync(e, RootPassword(), ct);
        await using var command = new MySqlCommand("SHOW GLOBAL VARIABLES", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) values[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        return values;
    }

    /// <summary>Changes the password of every local root account, then stores it.</summary>
    public async Task SetRootPasswordAsync(DbEngine e, string newPwd, CancellationToken ct)
    {
        await using var connection = await OpenAsync(e, RootPassword(), ct);
        var hosts = new List<string>();
        await using (var select = new MySqlCommand("SELECT Host FROM mysql.user WHERE User = 'root'", connection))
        await using (var reader = await select.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) hosts.Add(reader.GetString(0));

        foreach (var host in hosts)
        {
            await using var alter = new MySqlCommand($"ALTER USER 'root'@'{MySqlHelper.EscapeString(host)}' IDENTIFIED BY @pwd", connection);
            alter.Parameters.AddWithValue("@pwd", newPwd);
            await alter.ExecuteNonQueryAsync(ct);
        }
        secrets.SaveRootPassword(newPwd);
    }

    /// <summary>True when root logs in to the running server with this password.</summary>
    public async Task<bool> CanLogInAsync(DbEngine e, string password, CancellationToken ct)
    {
        try
        {
            await using var connection = await OpenAsync(e, password, ct);
            return true;
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.AccessDenied)
        {
            return false;
        }
    }

    private string RootPassword() =>
        secrets.LoadRootPassword() ?? throw new InvalidOperationException("The database root password is not stored. Install the database first.");

    private async Task<MySqlConnection> OpenAsync(DbEngine e, string password, CancellationToken ct)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1",
            Port = (uint)Port(e),
            UserID = "root",
            Password = password,
            // Management connections are rare and outlive server restarts badly.
            Pooling = false,
            // Loopback only: MySQL 8.4's caching_sha2_password needs TLS or the server's public key.
            AllowPublicKeyRetrieval = true,
        };
        var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private async Task AdminShutdownAsync(DbEngine e, string version, string password, CancellationToken ct)
    {
        string? options = null;
        try
        {
            options = DbClientOptions.Write(paths, password, Port(e));
            // --defaults-file (not -extra-file) so option files elsewhere on the machine cannot change where this goes.
            await RunToolAsync(Path.Combine(Bin(e, version), AdminExe(e)), [$"--defaults-file={options}", "shutdown"], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The supervisor stops the process after the grace period instead.
            Log.Warning(ex, "Clean database shutdown failed");
        }
        finally
        {
            if (options is not null) File.Delete(options);
        }
    }

    private static async Task RunToolAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ToolLimit);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(exe)} did not finish within {ToolLimit.TotalMinutes:0} minutes.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed (exit {process.ExitCode}). {((await stdout) + (await stderr)).Trim()}");
    }
}
