using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core.Redis;

namespace Anvil.App.Tests;

public sealed class RedisViewModelTests
{
    private readonly ScriptedWsl _wsl = new();
    private readonly FakeTextClipboard _clipboard = new();
    private readonly List<string> _opened = [];
    private readonly Queue<RedisPing> _pings = new();
    private readonly List<string?> _pingPasswords = [];

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private RedisViewModel Vm() => new(new WslRedis(_wsl), _clipboard, _opened.Add, ui: a => a(),
        ping: (_, _, password, _) =>
        {
            _pingPasswords.Add(password);
            return Task.FromResult(_pings.Count > 0 ? _pings.Dequeue() : new RedisPing(false, "Could not connect to 127.0.0.1:6379: refused"));
        },
        delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Without_wsl_the_page_shows_how_to_install_it()
    {
        _wsl.Exists = false;
        var vm = Vm();

        await vm.LoadAsync(Ct);

        Assert.True(vm.IsWslMissing);
        Assert.Contains(RedisViewModel.WslSteps, s => s.Command == "wsl --install -d Ubuntu");
        vm.OpenUrlCommand.Execute(RedisViewModel.WslInstallUrl);
        Assert.Equal(["https://learn.microsoft.com/windows/wsl/install"], _opened);
    }

    [Fact]
    public async Task Wsl_without_ubuntu_asks_for_ubuntu()
    {
        _wsl.Replies["--list"] = new(0, "kali-linux\n");
        var vm = Vm();

        await vm.LoadAsync(Ct);

        Assert.True(vm.IsNoUbuntu);
        Assert.Empty(vm.Credentials);
    }

    [Fact]
    public async Task One_click_installs_starts_and_tests_redis()
    {
        _wsl.Replies["--list"] = new(0, "Ubuntu\n");
        _wsl.Probes.Enqueue(new(0, "id=ubuntu\n"));
        _wsl.Probes.Enqueue(new(0, "id=ubuntu\ninstalled=yes\nrequirepass Pw123\n"));
        _wsl.Replies["install"] = new(0, "> apt-get update\nSetting up redis-server\n");
        var vm = Vm();
        await vm.LoadAsync(Ct);
        Assert.True(vm.IsNotInstalled);
        _pings.Enqueue(new(false, "refused"));
        _pings.Enqueue(new(true, "Redis answers on 127.0.0.1:6379."));

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.True(vm.IsRunning);
        Assert.Equal("Redis answers on 127.0.0.1:6379.", vm.Message);
        Assert.True(vm.MessageOk);
        Assert.Single(_wsl.Detached);
        Assert.Equal(["Pw123", "Pw123"], _pingPasswords);
        Assert.Equal(["Host", "Port", "Password", "URL", "Laravel .env"], vm.Credentials.Select(c => c.Label));
        Assert.Equal("redis://default:Pw123@127.0.0.1:6379", vm.Credentials.Single(c => c.Label == "URL").Value);
        Assert.Contains("> apt-get update", vm.InstallLog);
    }

    [Fact]
    public async Task A_failed_install_shows_its_output_and_the_manual_steps()
    {
        _wsl.Replies["--list"] = new(0, "Ubuntu\n");
        _wsl.Probes.Enqueue(new(0, "id=ubuntu\n"));
        _wsl.Replies["install"] = new(100, "E: Unable to locate package redis-server\n");
        var vm = Vm();
        await vm.LoadAsync(Ct);

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.True(vm.IsInstallFailed);
        Assert.True(vm.ShowLog);
        Assert.Contains("E: Unable to locate package redis-server", vm.InstallLog);
        Assert.Contains("exit code 100", vm.Detail);
        Assert.Contains(RedisViewModel.ManualSteps, s => s.Command?.Contains("apt-get install -y redis-server", StringComparison.Ordinal) == true);
        Assert.Empty(_wsl.Detached);
    }

    [Fact]
    public async Task Redis_that_never_answers_shows_the_debugging_steps()
    {
        _wsl.Replies["--list"] = new(0, "Ubuntu\n");
        _wsl.Probes.Enqueue(new(0, "id=ubuntu\ninstalled=yes\n"));
        var vm = Vm();
        await vm.LoadAsync(Ct);
        Assert.True(vm.IsStopped);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsNotAnswering);
        Assert.Equal("Could not connect to 127.0.0.1:6379: refused", vm.Detail);
        Assert.Equal(11, _pingPasswords.Count);
        Assert.Contains(RedisViewModel.DebugSteps, s => s.Command == "netstat -ano | findstr :6379");
    }

    [Fact]
    public async Task Running_redis_is_found_and_its_details_copied()
    {
        _wsl.Replies["--list"] = new(0, "Ubuntu-24.04\n");
        _wsl.Probes.Enqueue(new(0, "id=ubuntu\ninstalled=yes\nport 6380\nrequirepass Pw123\n"));
        _pings.Enqueue(new(true, "Redis answers on 127.0.0.1:6380."));
        var vm = Vm();

        await vm.LoadAsync(Ct);
        await vm.CopyCommand.ExecuteAsync(vm.Credentials.Single(c => c.Label == "Password").Value);

        Assert.True(vm.IsRunning);
        Assert.Equal("Ubuntu-24.04", vm.Distro);
        Assert.Equal("Pw123", _clipboard.Text);
        Assert.Equal("Copied.", vm.Message);
        Assert.Contains("REDIS_PORT=6380", vm.Credentials.Single(c => c.Label == "Laravel .env").Value);
    }

    [Fact]
    public async Task Later_visits_keep_the_page_until_check_again()
    {
        _wsl.Exists = false;
        var vm = Vm();

        await vm.LoadAsync(Ct);
        _wsl.Exists = true;
        _wsl.Replies["--list"] = new(0, "kali-linux\n");
        await vm.LoadAsync(Ct);
        Assert.True(vm.IsWslMissing);

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.IsNoUbuntu);
    }

    private sealed class ScriptedWsl : IWsl
    {
        public bool Exists { get; set; } = true;
        public Dictionary<string, WslResult> Replies { get; } = [];
        public Queue<WslResult> Probes { get; } = new();
        public List<string> Detached { get; } = [];

        public Task<WslResult> RunAsync(IReadOnlyList<string> args, string? stdin, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            WslResult reply;
            if (!args.Contains("sh")) reply = Replies.GetValueOrDefault(args[0], new WslResult(0, ""));
            else if (stdin!.Contains("apt-get install", StringComparison.Ordinal)) reply = Replies.GetValueOrDefault("install", new WslResult(0, ""));
            else if (stdin.Contains("os-release", StringComparison.Ordinal)) reply = Probes.Count > 0 ? Probes.Dequeue() : new WslResult(0, "id=ubuntu\n");
            else reply = new WslResult(0, "");
            foreach (var line in reply.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) onLine?.Invoke(line);
            return Task.FromResult(reply);
        }

        public void StartDetached(IReadOnlyList<string> args, string stdin) => Detached.Add(stdin);
    }
}
