using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.System.Threading;

namespace ArbiPache.Core.Preflight;

public sealed record PortHolder(int Port, int Pid, string ProcessName, string? ExePath);

public interface IPortProbe
{
    /// <summary>The process listening on the TCP port (IPv4 or IPv6), or null when the port is free.</summary>
    PortHolder? HolderOf(int port);
}

/// <summary>Finds TCP listeners with GetExtendedTcpTable, which names the owning process.</summary>
public sealed class PortProbe : IPortProbe
{
    private const uint AfInet = 2, AfInet6 = 23;

    public PortHolder? HolderOf(int port)
    {
        var pid = ListenerPid(AfInet, port) ?? ListenerPid(AfInet6, port);
        return pid is { } p ? Describe(port, p) : null;
    }

    private static unsafe int? ListenerPid(uint family, int port)
    {
        uint size = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = new byte[Math.Max(size, 4)];
            size = (uint)buffer.Length;
            var error = (WIN32_ERROR)PInvoke.GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (error == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER) continue;
            if (error != WIN32_ERROR.NO_ERROR) return null;
            fixed (byte* table = buffer)
            {
                return family == AfInet ? FindV4((MIB_TCPTABLE_OWNER_PID*)table, port) : FindV6((MIB_TCP6TABLE_OWNER_PID*)table, port);
            }
        }
        return null;
    }

    private static unsafe int? FindV4(MIB_TCPTABLE_OWNER_PID* table, int port)
    {
        var rows = table->table.AsSpan((int)table->dwNumEntries);
        foreach (ref readonly var row in rows)
            if (HostPort(row.dwLocalPort) == port) return (int)row.dwOwningPid;
        return null;
    }

    private static unsafe int? FindV6(MIB_TCP6TABLE_OWNER_PID* table, int port)
    {
        var rows = table->table.AsSpan((int)table->dwNumEntries);
        foreach (ref readonly var row in rows)
            if (HostPort(row.dwLocalPort) == port) return (int)row.dwOwningPid;
        return null;
    }

    // The port is in network byte order in the low 16 bits.
    private static int HostPort(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static PortHolder Describe(int port, int pid)
    {
        string name;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            name = $"process {pid}";
        }
        return new PortHolder(port, pid, name, ImagePath(pid));
    }

    // Limited-information access works for other users' processes too, unlike Process.MainModule.
    private static string? ImagePath(int pid)
    {
        using var handle = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsInvalid) return null;
        Span<char> buffer = stackalloc char[1024];
        var length = (uint)buffer.Length;
        if (!PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length)) return null;
        return new string(buffer[..(int)length]);
    }
}
