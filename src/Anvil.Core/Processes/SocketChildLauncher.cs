using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Anvil.Core.Processes;

/// <summary>
/// Starts a child whose stdin is a listening socket, with stdout and stderr set to INVALID_HANDLE_VALUE.
/// That is the only shape in which php-cgi on Windows runs as a FastCGI server on an inherited socket.
/// <see cref="Process.Start()"/> cannot pass a socket as stdin, so this calls CreateProcess directly.
/// </summary>
internal static class SocketChildLauncher
{
    /// <summary>
    /// Held around every supervised launch. A child started with inheritable handles (Process.Start does that)
    /// would otherwise pick up a pool socket's duplicate and keep the port open after the pool is gone.
    /// </summary>
    public static readonly Lock LaunchLock = new();

    /// <summary>Starts the child suspended, puts it in the job, then lets it run.</summary>
    public static unsafe Process Start(ProcessSpec spec, Socket socket, JobObject job)
    {
        lock (LaunchLock)
        {
            // The socket itself never becomes inheritable; only this short-lived duplicate does.
            var self = PInvoke.GetCurrentProcess();
            HANDLE duplicate;
            if (!PInvoke.DuplicateHandle(self, new HANDLE(socket.Handle), self, &duplicate, 0, true, DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS))
                throw new Win32Exception();
            try
            {
                return Start(spec, duplicate, job);
            }
            finally
            {
                PInvoke.CloseHandle(duplicate);
            }
        }
    }

    private static unsafe Process Start(ProcessSpec spec, HANDLE handle, JobObject job)
    {
        // Only the socket is inherited, not every inheritable handle the agent happens to hold.
        nuint size = 0;
        PInvoke.InitializeProcThreadAttributeList(default, 1, 0, &size);
        var attributes = Marshal.AllocHGlobal((nint)size);
        var list = new LPPROC_THREAD_ATTRIBUTE_LIST((void*)attributes);
        try
        {
            if (!PInvoke.InitializeProcThreadAttributeList(list, 1, 0, &size)) throw new Win32Exception();
            try
            {
                if (!PInvoke.UpdateProcThreadAttribute(list, 0, PInvoke.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, &handle, (nuint)sizeof(HANDLE), null, (nuint*)null))
                    throw new Win32Exception();

                var invalid = new HANDLE(-1);
                var info = new STARTUPINFOEXW { lpAttributeList = list };
                info.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
                info.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;
                info.StartupInfo.hStdInput = handle;
                info.StartupInfo.hStdOutput = invalid;
                info.StartupInfo.hStdError = invalid;

                var commandLine = (CommandLine(spec.Exe, spec.Args) + '\0').ToCharArray();
                var environment = EnvironmentBlock(spec.Env);
                PROCESS_INFORMATION pi;
                fixed (char* exe = spec.Exe)
                fixed (char* cmd = commandLine)
                fixed (char* env = environment)
                fixed (char* cwd = spec.WorkingDir)
                {
                    const PROCESS_CREATION_FLAGS flags = PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT | PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW
                        | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED | PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT;
                    if (!PInvoke.CreateProcess(exe, cmd, null, null, true, flags, env, cwd, &info.StartupInfo, &pi))
                        throw new Win32Exception();
                }

                try
                {
                    var process = Process.GetProcessById((int)pi.dwProcessId);
                    try
                    {
                        _ = process.SafeHandle; // open our own handle while the child cannot exit yet
                        job.Assign(process);
                    }
                    catch
                    {
                        PInvoke.TerminateProcess(pi.hProcess, 1);
                        process.Dispose();
                        throw;
                    }
                    PInvoke.ResumeThread(pi.hThread);
                    return process;
                }
                finally
                {
                    PInvoke.CloseHandle(pi.hThread);
                    PInvoke.CloseHandle(pi.hProcess);
                }
            }
            finally
            {
                PInvoke.DeleteProcThreadAttributeList(list);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(attributes);
        }
    }

    /// <summary>The agent's environment plus the spec's variables, as a sorted, double-NUL-terminated block.</summary>
    private static char[] EnvironmentBlock(IReadOnlyDictionary<string, string> extra)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) vars[(string)e.Key] = (string?)e.Value ?? "";
        foreach (var (key, value) in extra) vars[key] = value;
        var block = new StringBuilder();
        foreach (var (key, value) in vars) block.Append(key).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString().ToCharArray();
    }

    /// <summary>Quotes each argument the way the C runtime parses it back.</summary>
    internal static string CommandLine(string exe, IReadOnlyList<string> args)
    {
        var line = new StringBuilder();
        foreach (var arg in args.Prepend(exe))
        {
            if (line.Length > 0) line.Append(' ');
            if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
            {
                line.Append(arg);
                continue;
            }
            line.Append('"');
            var slashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                line.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
                slashes = 0;
            }
            line.Append('\\', slashes * 2).Append('"');
        }
        return line.ToString();
    }
}
