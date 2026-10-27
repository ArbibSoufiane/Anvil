using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ArbiPache.Core.Processes;

/// <summary>
/// Starts child processes inside a <see cref="JobObject"/> and keeps them running.
/// A clean exit (code 0) is a recycle and restarts at once. A crash restarts after 1, 2, 5, 10, then 30 s;
/// more than five crashes within a minute marks the process <see cref="ProcessState.Failed"/>.
/// </summary>
public sealed class ProcessSupervisor(JobObject job, TimeProvider time) : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    private const int MaxCrashes = 5;
    private static readonly TimeSpan CrashWindow = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on every state change. Handlers run on the supervisor's thread and must not block.</summary>
    public event EventHandler<ProcessStatus>? Changed;

    public void Start(ProcessSpec spec)
    {
        var entry = new Entry(spec, new ProcessStatus(spec.Name, ProcessState.Starting, null, null, 0, null));
        if (!_entries.TryAdd(spec.Name, entry))
        {
            var existing = _entries[spec.Name];
            if (existing.Status.State is not (ProcessState.Stopped or ProcessState.Failed))
                throw new InvalidOperationException($"{spec.Name} is already running.");
            existing.Dispose();
            _entries[spec.Name] = entry;
        }
        Launch(entry);
    }

    public async Task StopAsync(string name, TimeSpan grace, Func<Task>? gracefulStop = null)
    {
        if (!_entries.TryGetValue(name, out var e)) return;
        Process? p;
        lock (e.Lock)
        {
            e.Stopping = true;
            e.RestartCancel.Cancel();
            p = e.Process;
            e.Process = null; // StopAsync owns this process from here; OnExited leaves it alone
            if (p is null)
            {
                if (e.Status.State != ProcessState.Failed) Set(e, e.Status with { State = ProcessState.Stopped, Pid = null });
                return;
            }
        }

        if (gracefulStop is not null) await gracefulStop();
        try
        {
            await p.WaitForExitAsync().WaitAsync(grace, time);
        }
        catch (TimeoutException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await p.WaitForExitAsync();
        }

        p.Dispose();
        lock (e.Lock) Set(e, e.Status with { State = ProcessState.Stopped, Pid = null });
    }

    public IReadOnlyList<ProcessStatus> Snapshot() => _entries.Values.Select(e => e.Status).ToList();

    public static ProcessStartInfo BuildStartInfo(ProcessSpec spec)
    {
        var psi = new ProcessStartInfo(spec.Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = spec.WorkingDir,
        };
        foreach (var arg in spec.Args) psi.ArgumentList.Add(arg);
        foreach (var (key, value) in spec.Env) psi.Environment[key] = value;
        return psi;
    }

    private void Launch(Entry e)
    {
        lock (e.Lock)
        {
            if (e.Stopping) return;
            Set(e, e.Status with { State = ProcessState.Starting, Pid = null });
            var p = new Process { StartInfo = BuildStartInfo(e.Spec), EnableRaisingEvents = true };
            p.OutputDataReceived += (_, a) => e.Append(a.Data);
            p.ErrorDataReceived += (_, a) =>
            {
                if (string.IsNullOrWhiteSpace(a.Data)) return;
                e.LastStderr = a.Data;
                e.Append(a.Data);
            };
            p.Exited += (_, _) => OnExited(e, p);
            try
            {
                p.Start();
                job.Assign(p);
            }
            catch (Exception ex)
            {
                if (!p.HasExitedSafe()) try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                p.Dispose();
                Set(e, e.Status with { State = ProcessState.Failed, Pid = null, LastError = ex.Message });
                return;
            }
            e.Process = p;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            Set(e, e.Status with { State = ProcessState.Running, Pid = p.Id, StartedAt = time.GetUtcNow() });
        }
    }

    private void OnExited(Entry e, Process p)
    {
        lock (e.Lock)
            if (!ReferenceEquals(e.Process, p)) return; // StopAsync owns this exit

        p.WaitForExit(); // let redirected output drain so the last stderr line is known
        var code = p.ExitCode;
        lock (e.Lock)
        {
            if (!ReferenceEquals(e.Process, p)) return;
            e.Process = null;
            p.Dispose();
            e.Append($"[arbipache] {e.Spec.Name} exited with code {code}");

            if (e.Stopping || e.Spec.Restart == RestartPolicy.Never)
            {
                Set(e, e.Status with { State = ProcessState.Stopped, Pid = null, LastError = code == 0 ? null : e.LastStderr });
                return;
            }
            if (code == 0)
            {
                Launch(e);
                return;
            }

            var now = time.GetUtcNow();
            e.Crashes.Enqueue(now);
            while (e.Crashes.Peek() < now - CrashWindow) e.Crashes.Dequeue();
            var lastError = e.LastStderr ?? $"exited with code {code}";
            if (e.Crashes.Count > MaxCrashes)
            {
                Set(e, e.Status with { State = ProcessState.Failed, Pid = null, LastError = lastError });
                return;
            }

            // Arm the timer before announcing Restarting, so a listener that advances time cannot outrun it.
            var delay = Task.Delay(Backoff[Math.Min(e.Crashes.Count, Backoff.Length) - 1], time, e.RestartCancel.Token);
            Set(e, e.Status with { State = ProcessState.Restarting, Pid = null, Restarts = e.Status.Restarts + 1, LastError = lastError });
            _ = RestartAfterAsync(e, delay);
        }
    }

    private async Task RestartAfterAsync(Entry e, Task delay)
    {
        try
        {
            await delay.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Launch(e);
    }

    private void Set(Entry e, ProcessStatus status)
    {
        e.Status = status;
        Changed?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var name in _entries.Keys.ToList())
            await StopAsync(name, TimeSpan.Zero);
        foreach (var e in _entries.Values) e.Dispose();
    }

    private sealed class Entry(ProcessSpec spec, ProcessStatus status) : IDisposable
    {
        private readonly Lock _logLock = new();
        private StreamWriter? _log;

        public readonly Lock Lock = new();
        public readonly Queue<DateTimeOffset> Crashes = new();
        public readonly CancellationTokenSource RestartCancel = new();
        public ProcessSpec Spec { get; } = spec;
        public ProcessStatus Status { get; set; } = status;
        public Process? Process { get; set; }
        public bool Stopping { get; set; }
        public string? LastStderr { get; set; }

        public void Append(string? line)
        {
            if (line is null) return;
            lock (_logLock)
            {
                if (_log is null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Spec.LogFile))!);
                    var stream = new FileStream(Spec.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _log = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                }
                _log.WriteLine(line);
            }
        }

        public void Dispose()
        {
            RestartCancel.Cancel();
            RestartCancel.Dispose();
            lock (_logLock) _log?.Dispose();
        }
    }
}

internal static class ProcessExtensions
{
    public static bool HasExitedSafe(this Process p)
    {
        try { return p.HasExited; } catch (InvalidOperationException) { return true; }
    }
}
