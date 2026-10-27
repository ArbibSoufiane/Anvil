using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ArbiPache.Core.Processes;
using Microsoft.Extensions.Time.Testing;

namespace ArbiPache.Core.Tests.Processes;

public sealed class ProcessSupervisorTests : IDisposable
{
    private static readonly string Child = Path.Combine(AppContext.BaseDirectory, "ArbiPache.TestChild.exe");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ap-sup-" + Guid.NewGuid().ToString("N"));
    private readonly BlockingCollection<ProcessStatus> _events = new();

    public ProcessSupervisorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ProcessSpec Spec(string name, params string[] args) =>
        new(name, Child, args, new Dictionary<string, string>(), _dir, Path.Combine(_dir, name + ".log"));

    // The supervisor keeps its log open for writing; readers must share it, as the Logs page will.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    private ProcessSupervisor Supervisor(TimeProvider time)
    {
        var supervisor = new ProcessSupervisor(new JobObject(), time);
        supervisor.Changed += (_, s) => _events.Add(s);
        return supervisor;
    }

    private ProcessStatus WaitFor(Func<ProcessStatus, bool> match, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
            if (_events.TryTake(out var s, TimeSpan.FromMilliseconds(100)) && match(s))
                return s;
        throw new TimeoutException("Expected process status was not reported.");
    }

    [Fact]
    public async Task Crashing_child_is_restarted()
    {
        await using var supervisor = Supervisor(TimeProvider.System);

        supervisor.Start(Spec("crash", "--exit-after", "100", "--code", "3"));
        var first = WaitFor(s => s.State == ProcessState.Running);
        var again = WaitFor(s => s.State == ProcessState.Running && s.Pid != first.Pid);

        Assert.Equal(1, again.Restarts);
        Assert.Contains("child exiting with 3", ReadShared(Path.Combine(_dir, "crash.log")));
    }

    [Fact]
    public async Task Five_crashes_in_a_minute_marks_failed()
    {
        var time = new FakeTimeProvider();
        await using var supervisor = Supervisor(time);

        supervisor.Start(Spec("flaky", "--code", "7"));
        // Five restarts after 1, 2, 5, 10 and 30 s of backoff; the sixth crash, 48 s in, is one too many.
        foreach (var backoff in new[] { 1, 2, 5, 10, 30 })
        {
            WaitFor(s => s.State == ProcessState.Restarting);
            time.Advance(TimeSpan.FromSeconds(backoff));
        }
        var failed = WaitFor(s => s.State == ProcessState.Failed);

        Assert.Equal("child exiting with 7", failed.LastError);
        Assert.Equal(5, failed.Restarts);
        Assert.Null(failed.Pid);
    }

    [Fact]
    public async Task Crashes_spread_over_more_than_a_minute_keep_restarting()
    {
        var time = new FakeTimeProvider();
        await using var supervisor = Supervisor(time);

        supervisor.Start(Spec("slow-flaky", "--code", "7"));
        for (var i = 0; i < 7; i++)
        {
            WaitFor(s => s.State == ProcessState.Restarting);
            time.Advance(TimeSpan.FromSeconds(31));
        }

        Assert.NotEqual(ProcessState.Failed, supervisor.Snapshot().Single().State);
    }

    [Fact]
    public async Task Clean_exit_is_recycled_not_counted()
    {
        await using var supervisor = Supervisor(TimeProvider.System);

        supervisor.Start(Spec("recycle", "--exit-after", "200", "--code", "0"));
        var first = WaitFor(s => s.State == ProcessState.Running);
        var second = WaitFor(s => s.State == ProcessState.Running && s.Pid != first.Pid);

        Assert.Equal(0, second.Restarts);
    }

    [Fact]
    public async Task Never_policy_does_not_restart()
    {
        await using var supervisor = Supervisor(TimeProvider.System);

        supervisor.Start(Spec("once", "--code", "2") with { Restart = RestartPolicy.Never });
        var stopped = WaitFor(s => s.State == ProcessState.Stopped);

        Assert.Equal(0, stopped.Restarts);
    }

    [Fact]
    public async Task StopAsync_kills_after_grace()
    {
        await using var supervisor = Supervisor(TimeProvider.System);
        supervisor.Start(Spec("sleeper", "--sleep"));
        var running = WaitFor(s => s.State == ProcessState.Running);
        var gracefulCalled = false;
        var watch = Stopwatch.StartNew();

        await supervisor.StopAsync("sleeper", TimeSpan.FromMilliseconds(300), () => { gracefulCalled = true; return Task.CompletedTask; });

        Assert.True(gracefulCalled);
        Assert.InRange(watch.ElapsedMilliseconds, 250, 5000);
        Assert.Equal(ProcessState.Stopped, supervisor.Snapshot().Single().State);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(running.Pid!.Value));
    }

    [Fact]
    public void Disposing_job_kills_children()
    {
        var job = new JobObject();
        using var child = Process.Start(ProcessSupervisor.BuildStartInfo(Spec("orphan", "--sleep")))!;
        job.Assign(child);

        job.Dispose();

        Assert.True(child.WaitForExit(5000));
    }

    [Fact]
    public void StartInfo_has_no_window_and_no_shell()
    {
        var spec = Spec("php", "-b", "127.0.0.1:9100") with { Env = new Dictionary<string, string> { ["PHP_FCGI_MAX_REQUESTS"] = "500" } };

        var psi = ProcessSupervisor.BuildStartInfo(spec);

        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.Equal(["-b", "127.0.0.1:9100"], psi.ArgumentList);
        Assert.Equal("500", psi.Environment["PHP_FCGI_MAX_REQUESTS"]);
        Assert.Equal(_dir, psi.WorkingDirectory);
    }

    private static Socket Listen()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen(16);
        return socket;
    }

    private static async Task<string> AskAsync(Socket listener, Task? beforeReading = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!, TestContext.Current.CancellationToken);
        if (beforeReading is not null) await beforeReading;
        using var reader = new StreamReader(client.GetStream());
        return await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken) ?? "";
    }

    [Fact]
    public async Task Socket_child_inherits_listen_socket_as_stdin()
    {
        using var listener = Listen();
        await using var supervisor = Supervisor(TimeProvider.System);

        supervisor.Start(Spec("sock", "--accept-stdin") with { StdinListenSocket = listener });
        var running = WaitFor(s => s.State == ProcessState.Running);

        Assert.Equal($"hello from {running.Pid}", await AskAsync(listener));
        Assert.Contains("hello from", await AskAsync(listener), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Socket_survives_worker_restart()
    {
        using var listener = Listen();
        await using var supervisor = Supervisor(TimeProvider.System);
        var spec = Spec("sock", "--accept-stdin") with { StdinListenSocket = listener };
        supervisor.Start(spec);
        var first = WaitFor(s => s.State == ProcessState.Running);
        await supervisor.StopAsync("sock", TimeSpan.Zero);
        WaitFor(s => s.State == ProcessState.Stopped);

        // Nobody accepts now, but the agent still owns the socket: the connection waits in its backlog.
        var reply = await AskAsync(listener, beforeReading: Task.Run(() => supervisor.Start(spec), TestContext.Current.CancellationToken));

        var second = WaitFor(s => s.State == ProcessState.Running);
        Assert.NotEqual(first.Pid, second.Pid);
        Assert.Equal($"hello from {second.Pid}", reply);
    }

    [Fact]
    public async Task Socket_child_exit_code_reported()
    {
        using var listener = Listen();
        await using var supervisor = Supervisor(TimeProvider.System);

        supervisor.Start(Spec("sock", "--exit-after", "0", "--code", "3") with { StdinListenSocket = listener, Restart = RestartPolicy.Never });

        var stopped = WaitFor(s => s.State == ProcessState.Stopped);
        Assert.Equal("exited with code 3", stopped.LastError);
    }
}
