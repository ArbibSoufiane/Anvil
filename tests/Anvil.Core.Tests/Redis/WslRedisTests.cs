using System.Net;
using System.Net.Sockets;
using System.Text;
using Anvil.Core.Redis;

namespace Anvil.Core.Tests.Redis;

/// <summary>Answers wsl.exe calls from a script of replies, keyed by the first argument (or "sh" for scripts run in a distribution).</summary>
public sealed class FakeWsl : IWsl
{
    public bool Exists { get; set; } = true;
    public Dictionary<string, WslResult> Replies { get; } = [];
    public List<(IReadOnlyList<string> Args, string? Stdin)> Calls { get; } = [];
    public List<string> Detached { get; } = [];

    public Task<WslResult> RunAsync(IReadOnlyList<string> args, string? stdin, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((args, stdin));
        var key = args.Contains("sh") ? stdin!.Contains("apt-get install", StringComparison.Ordinal) ? "install" : "probe" : args[0];
        var reply = Replies.GetValueOrDefault(key, new WslResult(0, ""));
        foreach (var line in reply.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) onLine?.Invoke(line);
        return Task.FromResult(reply);
    }

    public void StartDetached(IReadOnlyList<string> args, string stdin) => Detached.Add(stdin);
}

public sealed class WslRedisTests
{
    private readonly FakeWsl _wsl = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_wsl_exe_means_wsl_is_missing()
    {
        _wsl.Exists = false;

        var state = await new WslRedis(_wsl).DetectAsync(Ct);

        Assert.Equal(WslKind.Missing, state.Kind);
        Assert.Empty(_wsl.Calls);
    }

    [Fact]
    public async Task A_failing_list_and_status_mean_wsl_is_missing()
    {
        _wsl.Replies["--list"] = new(1, "The Windows Subsystem for Linux is not installed.");
        _wsl.Replies["--status"] = new(1, "");

        var state = await new WslRedis(_wsl).DetectAsync(Ct);

        Assert.Equal(WslKind.Missing, state.Kind);
        Assert.Equal("The Windows Subsystem for Linux is not installed.", state.Detail);
    }

    [Fact]
    public async Task Wsl_without_any_distribution_needs_ubuntu()
    {
        _wsl.Replies["--list"] = new(1, "Windows Subsystem for Linux has no installed distributions.");

        var state = await new WslRedis(_wsl).DetectAsync(Ct);

        Assert.Equal(WslKind.NoUbuntu, state.Kind);
    }

    [Fact]
    public async Task Other_distributions_are_not_ubuntu()
    {
        _wsl.Replies["--list"] = new(0, "docker-desktop\r\nkali-linux\r\n");

        var state = await new WslRedis(_wsl).DetectAsync(Ct);

        Assert.Equal(WslKind.NoUbuntu, state.Kind);
        Assert.Single(_wsl.Calls);
    }

    [Fact]
    public async Task Ubuntu_without_redis_is_ready_to_install()
    {
        _wsl.Replies["--list"] = new(0, "docker-desktop\r\nUbuntu-24.04\r\n");
        _wsl.Replies["probe"] = new(0, "id=ubuntu\n");

        var state = await new WslRedis(_wsl).DetectAsync(Ct);

        Assert.Equal(new WslRedisState(WslKind.Ready, "Ubuntu-24.04"), state);
        Assert.Equal(["--distribution", "Ubuntu-24.04", "--user", "root", "--exec", "sh", "-s"], _wsl.Calls[1].Args);
    }

    [Fact]
    public async Task Installed_redis_reports_its_port_and_password_from_redis_conf()
    {
        _wsl.Replies["--list"] = new(0, "Ubuntu\n");
        _wsl.Replies["probe"] = new(0, "id=ubuntu\ninstalled=yes\nport 6380\nrequirepass \"s3cret\"\n");

        var state = await new WslRedis(_wsl).DetectAsync(Ct);

        Assert.Equal(new WslRedisState(WslKind.Ready, "Ubuntu", RedisInstalled: true, Port: 6380, Password: "s3cret"), state);
    }

    [Fact]
    public async Task A_distribution_named_ubuntu_that_is_not_ubuntu_is_refused()
    {
        _wsl.Replies["--list"] = new(0, "Ubuntu\n");
        _wsl.Replies["probe"] = new(0, "id=debian\n");

        var state = await new WslRedis(_wsl).DetectAsync(Ct);

        Assert.Equal(WslKind.NoUbuntu, state.Kind);
    }

    [Fact]
    public async Task Install_runs_apt_as_root_and_keeps_an_existing_password()
    {
        await new WslRedis(_wsl).InstallAsync("Ubuntu", null, Ct);

        var (args, stdin) = Assert.Single(_wsl.Calls);
        Assert.Equal(["--distribution", "Ubuntu", "--user", "root", "--exec", "sh", "-s"], args);
        Assert.StartsWith("{\n", stdin);
        Assert.EndsWith("} </dev/null\n", stdin);
        Assert.DoesNotContain("\r", stdin);
        Assert.Contains("apt-get install -y redis-server", stdin);
        Assert.Contains("if ! grep -q '^requirepass ' /etc/redis/redis.conf; then", stdin);
        Assert.Contains("service redis-server restart", stdin);
    }

    [Fact]
    public void Start_leaves_a_keeper_running_that_ends_with_redis()
    {
        new WslRedis(_wsl).Start("Ubuntu");

        var script = Assert.Single(_wsl.Detached);
        Assert.Contains("service redis-server start", script);
        Assert.Contains("while service redis-server status", script);
    }

    [Fact]
    public void New_passwords_are_letters_and_digits()
    {
        var password = WslRedis.NewPassword();

        Assert.Equal(24, password.Length);
        Assert.All(password, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        Assert.NotEqual(password, WslRedis.NewPassword());
    }

    [Fact]
    public void Wsl_output_is_read_as_utf16_or_utf8()
    {
        Assert.Equal("Ubuntu\r\n", WslCli.Decode(Encoding.Unicode.GetBytes("Ubuntu\r\n")));
        Assert.Equal("id=ubuntu é\n", WslCli.Decode(Encoding.UTF8.GetBytes("id=ubuntu é\n")));
    }
}

public sealed class RedisProbeTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // A one-connection Redis that answers each command with the next reply.
    private static (int Port, Task<List<string>> Received) Serve(params string[] replies)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            using var _ = listener.Server;
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            var commands = new List<string>();
            foreach (var reply in replies)
            {
                // *N, then $len and the value for each part.
                var count = int.Parse((await reader.ReadLineAsync())![1..]);
                var parts = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    await reader.ReadLineAsync();
                    parts.Add((await reader.ReadLineAsync())!);
                }
                commands.Add(string.Join(' ', parts));
                await stream.WriteAsync(Encoding.UTF8.GetBytes(reply + "\r\n"));
            }
            listener.Stop();
            return commands;
        });
        return (port, received);
    }

    [Fact]
    public async Task Logs_in_and_pings()
    {
        var (port, received) = Serve("+OK", "+PONG");

        var ping = await RedisProbe.PingAsync("127.0.0.1", port, "s3cret", TimeSpan.FromSeconds(5), Ct);

        Assert.True(ping.Ok, ping.Message);
        Assert.Equal(["AUTH s3cret", "PING"], await received);
    }

    [Fact]
    public async Task A_wrong_password_says_so()
    {
        var (port, _) = Serve("-WRONGPASS invalid username-password pair or user is disabled.");

        var ping = await RedisProbe.PingAsync("127.0.0.1", port, "nope", TimeSpan.FromSeconds(5), Ct);

        Assert.False(ping.Ok);
        Assert.StartsWith("Redis did not accept the password: WRONGPASS", ping.Message);
    }

    [Fact]
    public async Task No_password_skips_auth()
    {
        var (port, received) = Serve("+PONG");

        var ping = await RedisProbe.PingAsync("127.0.0.1", port, null, TimeSpan.FromSeconds(5), Ct);

        Assert.True(ping.Ok, ping.Message);
        Assert.Equal(["PING"], await received);
    }

    [Fact]
    public async Task Nothing_listening_fails_with_a_reason()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var ping = await RedisProbe.PingAsync("127.0.0.1", port, null, TimeSpan.FromSeconds(5), Ct);

        Assert.False(ping.Ok);
        Assert.StartsWith($"Could not connect to 127.0.0.1:{port}", ping.Message);
    }
}
