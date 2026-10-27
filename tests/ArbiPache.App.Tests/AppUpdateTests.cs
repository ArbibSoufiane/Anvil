using System.ComponentModel;
using ArbiPache.App.Services;
using ArbiPache.App.Tests.Support;
using ArbiPache.Core;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Update;

namespace ArbiPache.App.Tests;

/// <summary>Handing the window over to the staged ArbiPache.exe that replaces the program files.</summary>
public sealed class AppUpdateTests
{
    private const string Staged = @"D:\ArbiPache\.update\staged-4.1.0";
    private static readonly AppPaths Paths = new(@"D:\ArbiPache");
    private static readonly Release Next = new("4.1.0", new Uri("https://github.com/ArbibSoufiane/ArbiPache/releases/download/v4.1.0/ArbiPache-4.1.0-win64.zip"),
        new Uri("https://github.com/ArbibSoufiane/ArbiPache/releases/download/v4.1.0/SHA256SUMS"), "Faster starts.");

    private readonly FakeAgentApi _api = new();
    private readonly FakeAgentLink _link;
    private Func<Task<string>> _stage = () => Task.FromResult(Staged);
    private Exception? _launchFails;
    private (string Exe, IReadOnlyList<string> Args)? _launched;

    public AppUpdateTests() => _link = new FakeAgentLink(_api.Calls);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AppUpdate Update(AppPaths? paths = null) => new(paths ?? Paths, _api, _link,
        _ => Task.FromResult<Release?>(Next),
        (r, _, _) =>
        {
            _api.Calls.Add($"stage {r.Version}");
            return _stage();
        },
        (exe, args) =>
        {
            _api.Calls.Add("launch");
            if (_launchFails is not null) throw _launchFails;
            _launched = (exe, args);
        },
        () => _api.Calls.Add("exit"));

    [Fact]
    public async Task Install_stops_the_agent_then_hands_over_to_the_staged_copy()
    {
        var problem = await Update().InstallAsync(Next, null, Ct);

        Assert.Null(problem);
        Assert.Equal(["stage 4.1.0", "hold", "shutdown True", "gone", "launch", "exit"], _api.Calls);
        Assert.Equal(Path.Combine(Staged, "ArbiPache.exe"), _launched!.Value.Exe);
        Assert.Equal(new ApplyUpdateArgs(Staged, Paths.Root, Environment.ProcessId), ApplyUpdateArgs.Parse(_launched.Value.Args));
    }

    [Fact]
    public async Task A_refused_download_leaves_the_agent_running()
    {
        _stage = () => throw new ChecksumMismatchException("aa", "bb");

        var problem = await Update().InstallAsync(Next, null, Ct);

        Assert.Equal("ArbiPache 4.1.0 was not installed: Download checksum mismatch: expected SHA-256 aa, got bb. The file was deleted.", problem);
        Assert.Equal(["stage 4.1.0"], _api.Calls);
    }

    [Fact]
    public async Task A_failed_hand_over_starts_the_agent_again()
    {
        _launchFails = new Win32Exception("Operation did not complete successfully because the file contains a virus.");

        var problem = await Update().InstallAsync(Next, null, Ct);

        Assert.Equal(["stage 4.1.0", "hold", "shutdown True", "gone", "launch", "release", "connect"], _api.Calls);
        Assert.Equal("ArbiPache 4.1.0 was not installed: Operation did not complete successfully because the file contains a virus. "
            + $"ArbiPache {BuildInfo.Version} keeps running.", problem);
    }

    [Fact]
    public async Task An_agent_that_does_not_stop_keeps_the_window()
    {
        var link = new StuckLink(_api.Calls);
        var update = new AppUpdate(Paths, _api, link, _ => Task.FromResult<Release?>(Next), (_, _, _) => Task.FromResult(Staged),
            (_, _) => _api.Calls.Add("launch"), () => _api.Calls.Add("exit"));

        var problem = await update.InstallAsync(Next, null, Ct);

        Assert.Equal(["hold", "shutdown True", "release", "connect"], _api.Calls);
        Assert.Equal($"ArbiPache 4.1.0 was not installed: The ArbiPache Agent did not stop within a minute. ArbiPache {BuildInfo.Version} keeps running.", problem);
    }

    [Fact]
    public void A_development_build_does_not_update_itself()
    {
        Assert.Null(Update().CannotUpdate);
        Assert.Equal("This copy of ArbiPache was built from source, so it does not update itself. Build the new version instead.",
            Update(new AppPaths(@"D:\src\ArbiPache\src\ArbiPache.App\bin\Debug\net10.0-windows\win-x64")).CannotUpdate);
        Assert.NotNull(Update(new AppPaths(@"D:\src\ArbiPache\src\ArbiPache.App\bin\Release\net10.0-windows\win-x64")).CannotUpdate);
    }

    private sealed class StuckLink(List<string> calls) : IAgentLink
    {
        private readonly FakeAgentLink _inner = new(calls);

        public IDisposable HoldStart() => _inner.HoldStart();

        public Task WhenAgentGoneAsync(CancellationToken ct) => throw new TimeoutException("The ArbiPache Agent did not stop within a minute.");

        public Task ConnectAsync(CancellationToken ct) => _inner.ConnectAsync(ct);
    }
}
