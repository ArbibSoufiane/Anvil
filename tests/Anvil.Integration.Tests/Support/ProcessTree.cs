using System.Runtime.InteropServices;

namespace Anvil.Integration.Tests.Support;

/// <summary>Running processes with their parents, from a Toolhelp snapshot.</summary>
internal static class ProcessTree
{
    public sealed record Entry(int Id, int ParentId, string Name);

    private const uint SnapProcess = 0x2;

    public static IReadOnlyList<Entry> All()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == -1) throw new InvalidOperationException($"No process snapshot (error {Marshal.GetLastPInvokeError()}).");
        try
        {
            var entries = new List<Entry>();
            var e = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref e); more; more = Process32NextW(snapshot, ref e))
                entries.Add(new Entry((int)e.ProcessId, (int)e.ParentProcessId, e.ExeFile));
            return entries;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>The processes <paramref name="root"/> started, and the ones those started, at any depth.</summary>
    public static IReadOnlyList<Entry> Below(int root)
    {
        var all = All();
        var parents = new HashSet<int> { root };
        var found = new List<Entry>();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var e in all)
            {
                if (!parents.Contains(e.ParentId) || !parents.Add(e.Id)) continue;
                found.Add(e);
                grew = true;
            }
        }
        return found;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
