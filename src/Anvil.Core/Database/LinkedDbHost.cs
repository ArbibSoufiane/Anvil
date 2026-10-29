using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using Anvil.Core.Paths;
using Anvil.Core.Processes;
using MySqlConnector;
using Serilog;
using Windows.Win32;
using Windows.Win32.System.Services;

namespace Anvil.Core.Database;

/// <summary>The real <see cref="ILinkedDbHost"/>: Windows' service manager, or the Agent's process supervisor.</summary>
public sealed class LinkedDbHost(AppPaths paths, ProcessSupervisor supervisor, TimeProvider time) : ILinkedDbHost
{
    private const uint ScManagerConnect = 0x0001;
    private const uint QueryStatus = 0x0004;
    private const uint Start = 0x0010;
    private const uint Stop = 0x0020;
    private const uint ControlStop = 0x0001;
    private const int AccessDenied = 5;
    private const int AlreadyRunning = 1056;
    private const int NotActive = 1062;
    private static readonly TimeSpan ReadyLimit = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(60);

    public DbFolderInfo Inspect(string binDir) =>
        DbFolder.Inspect(binDir, WindowsServices.All(), exe => FileVersionInfo.GetVersionInfo(exe).ProductVersion);

    public ProcessState State(LinkedDatabase server)
    {
        if (server.Service is null)
            return supervisor.Snapshot().FirstOrDefault(s => s.Name == ProcessName(server))?.State ?? ProcessState.Stopped;
        using var service = Open(server.Service, QueryStatus, out _);
        if (service is null || !PInvoke.QueryServiceStatus(service, out var status)) return ProcessState.Stopped;
        return status.dwCurrentState switch
        {
            SERVICE_STATUS_CURRENT_STATE.SERVICE_RUNNING => ProcessState.Running,
            SERVICE_STATUS_CURRENT_STATE.SERVICE_START_PENDING or SERVICE_STATUS_CURRENT_STATE.SERVICE_CONTINUE_PENDING => ProcessState.Starting,
            _ => ProcessState.Stopped,
        };
    }

    public bool MayControl(LinkedDatabase server)
    {
        if (server.Service is null) return true;
        using var service = Open(server.Service, Start | Stop, out _);
        return service is not null;
    }

    public async Task StartAsync(LinkedDatabase server, CancellationToken ct)
    {
        if (server.Service is { } name)
        {
            using (var service = Open(name, Start, out var error) ?? throw Denied(name, error))
            {
                if (!PInvoke.StartService(service, null) && Marshal() is var e and not AlreadyRunning) throw new Win32Exception(e);
            }
            await WaitForAsync(server, ProcessState.Running, ct);
            return;
        }

        supervisor.Start(Spec(server));
        await WaitUntilReadyAsync(server, ct);
    }

    public async Task StopAsync(LinkedDatabase server, string? password, CancellationToken ct)
    {
        if (server.Service is { } name)
        {
            using (var service = Open(name, Stop, out var error) ?? throw Denied(name, error))
            {
                if (!PInvoke.ControlService(service, ControlStop, out _) && Marshal() is var e and not NotActive) throw new Win32Exception(e);
            }
            await WaitForAsync(server, ProcessState.Stopped, ct);
            return;
        }

        await supervisor.StopAsync(ProcessName(server), password is null ? TimeSpan.FromSeconds(5) : ShutdownGrace,
            password is null ? null : () => AdminShutdownAsync(server, password, ct));
    }

    public async Task<bool> CanLogInAsync(LinkedDatabase server, string password, CancellationToken ct)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1",
            Port = (uint)server.Port,
            UserID = server.User,
            Password = password,
            Pooling = false,
            // Loopback only: MySQL 8's caching_sha2_password needs TLS or the server's public key.
            AllowPublicKeyRetrieval = true,
        };
        try
        {
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);
            return true;
        }
        catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.AccessDenied)
        {
            return false;
        }
    }

    public Task<string> BackupAsync(LinkedDatabase server, string password, CancellationToken ct) =>
        new DbBackup(paths, new DbSecrets(paths), time).BackupLinkedAsync(server, password, ct);

    private static string ProcessName(LinkedDatabase server) => $"linked-{server.Id}";

    /// <summary>--defaults-file must come first; --port after it wins over the file's port.</summary>
    private ProcessSpec Spec(LinkedDatabase server)
    {
        var exe = new[] { "mariadbd.exe", "mysqld.exe" }.Select(n => Path.Combine(server.BinDir, n)).First(File.Exists);
        var args = new List<string>();
        if (server.DefaultsFile is { } file) args.Add($"--defaults-file={file}");
        args.Add($"--port={server.Port}");
        return new ProcessSpec(ProcessName(server), exe, args, new Dictionary<string, string>(), server.BinDir,
            Path.Combine(paths.Logs, "db", $"{ProcessName(server)}.log"));
    }

    private async Task WaitUntilReadyAsync(LinkedDatabase server, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < ReadyLimit)
        {
            ct.ThrowIfCancellationRequested();
            var status = supervisor.Snapshot().FirstOrDefault(s => s.Name == ProcessName(server));
            if (status is { State: ProcessState.Failed or ProcessState.Stopped or ProcessState.Restarting })
            {
                await supervisor.StopAsync(ProcessName(server), TimeSpan.FromSeconds(5));
                throw new InvalidOperationException($"The server stopped while starting. {status.LastError} See {Spec(server).LogFile}.");
            }
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync("127.0.0.1", server.Port, ct);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(250, ct);
            }
        }
        await supervisor.StopAsync(ProcessName(server), TimeSpan.FromSeconds(5));
        throw new TimeoutException($"The server did not accept connections on port {server.Port} within {ReadyLimit.TotalSeconds:0} seconds.");
    }

    private async Task WaitForAsync(LinkedDatabase server, ProcessState wanted, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (State(server) != wanted)
        {
            if (deadline.Elapsed > ReadyLimit)
                throw new TimeoutException($"{server.Service} did not {(wanted == ProcessState.Running ? "start" : "stop")} within {ReadyLimit.TotalSeconds:0} seconds.");
            await Task.Delay(250, ct);
        }
    }

    private async Task AdminShutdownAsync(LinkedDatabase server, string password, CancellationToken ct)
    {
        string? options = null;
        try
        {
            var admin = new[] { "mariadb-admin.exe", "mysqladmin.exe" }.Select(n => Path.Combine(server.BinDir, n)).First(File.Exists);
            options = DbClientOptions.Write(paths, password, server.Port, server.User);
            var psi = new ProcessStartInfo(admin) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add($"--defaults-file={options}");
            psi.ArgumentList.Add("shutdown");
            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The supervisor stops the process after the grace period instead.
            Log.Warning(e, "Clean shutdown of linked server {Id} failed", server.Id);
        }
        finally
        {
            if (options is not null) File.Delete(options);
        }
    }

    private static CloseServiceHandleSafeHandle? Open(string name, uint access, out int error)
    {
        using var manager = PInvoke.OpenSCManager(null, null, ScManagerConnect);
        var service = PInvoke.OpenService(manager, name, access);
        error = service.IsInvalid ? Marshal() : 0;
        if (!service.IsInvalid) return service;
        service.Dispose();
        return null;
    }

    private static int Marshal() => System.Runtime.InteropServices.Marshal.GetLastPInvokeError();

    private static Exception Denied(string service, int error) => error == AccessDenied
        ? new UnauthorizedAccessException($"Windows does not let this account start and stop {service}.")
        : new Win32Exception(error);
}
