using System.Diagnostics;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;

namespace ArbiPache.Core.Preflight;

/// <summary>
/// Checks what would stop Apache from starting, or make it fail later: a busy port, a URL reserved in HTTP.SYS,
/// a missing Visual C++ runtime, and a nearly full disk.
/// </summary>
public sealed class Preflight(IPortProbe probe, Func<string> netshHttpServiceState, VcRedistCheck vc, AppPaths paths,
    Func<string, long>? freeBytes = null)
{
    public const long LowDiskBytes = 1024L * 1024 * 1024;
    private const int SystemPid = 4;
    private readonly Func<string, long> _freeBytes = freeBytes ?? FreeBytesOnVolume;

    public IReadOnlyList<PreflightFinding> Run(AppSettings s)
    {
        var findings = new List<PreflightFinding>();
        var netsh = SafeNetsh();
        foreach (var (port, move) in Ports(s))
        {
            var httpSys = HttpSysParser.RegistrationsOnPort(netsh, port);
            if (httpSys.Count > 0)
                findings.Add(new PreflightFinding($"http-sys-{port}", Severity.Blocking,
                    $"Windows HTTP.SYS has URLs registered on port {port}: {string.Join(", ", httpSys)}.",
                    $"Stop the World Wide Web Publishing service (IIS) or the program that registered them, or {move}."));

            var holder = probe.HolderOf(port);
            if (holder is null || holder.IsApacheOf(paths)) continue;
            if (holder.Pid == SystemPid && httpSys.Count > 0) continue; // already reported, with more detail
            findings.Add(PortFinding(port, move, holder));
        }

        if (vc.Check() is var state and not VcRedistState.Ok)
            findings.Add(new PreflightFinding("vcredist", Severity.Blocking,
                state == VcRedistState.Missing
                    ? "The Microsoft Visual C++ x64 runtime is not installed. Apache and PHP need it."
                    : $"The Microsoft Visual C++ x64 runtime is older than {VcRedistCheck.Minimum}. Apache and PHP need {VcRedistCheck.Minimum} or newer.",
                $"Install the latest x64 package from {VcRedistCheck.DownloadPage}"));

        var free = _freeBytes(paths.Root);
        if (free < LowDiskBytes)
            findings.Add(new PreflightFinding("disk-space", Severity.Warning,
                $"Only {free / (1024 * 1024)} MB is free on {Path.GetPathRoot(paths.Root)}.",
                "Free up space: logs, backups and downloads need room to grow."));

        return findings;
    }

    /// <summary>Runs <c>netsh http show servicestate view=requestq verbose=no</c> and returns its output.</summary>
    public static string QueryHttpServiceState()
    {
        var psi = new ProcessStartInfo("netsh", ["http", "show", "servicestate", "view=requestq", "verbose=no"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);
        return output;
    }

    // Every port httpd binds: the two from Settings and any Listen line the user added to httpd.conf. Each comes with
    // how to move Apache off it.
    private IEnumerable<(int Port, string Move)> Ports(AppSettings s)
    {
        yield return (s.HttpPort, "change the HTTP port in Settings");
        if (s.HttpsPort != s.HttpPort) yield return (s.HttpsPort, "change the HTTPS port in Settings");
        var conf = File.Exists(paths.HttpdConf) ? File.ReadAllText(paths.HttpdConf) : "";
        foreach (var port in StockConfScanner.Scan(conf).ListenPorts.Where(p => p != s.HttpPort && p != s.HttpsPort).Order())
            yield return (port, $"remove \"Listen {port}\" from httpd.conf (open it from the Apache page)");
    }

    private static PreflightFinding PortFinding(int port, string move, PortHolder holder) =>
        holder.Pid == SystemPid
            ? new PreflightFinding($"port-{port}", Severity.Blocking,
                $"Port {port} is used by Windows HTTP.SYS (PID {SystemPid}), usually IIS (W3SVC).",
                $"Stop the World Wide Web Publishing service, or {move}.")
            : new PreflightFinding($"port-{port}", Severity.Blocking,
                $"Port {port} is used by {holder.ProcessName} (PID {holder.Pid}).",
                $"Close {holder.ProcessName}, or {move}.");

    // HTTP.SYS is only a hint; a failing netsh must not hide the other checks.
    private string SafeNetsh()
    {
        try { return netshHttpServiceState(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return ""; }
    }

    private static long FreeBytesOnVolume(string root) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace;
}
