using Anvil.App.Services;
using Anvil.Core.Elevation;
using Anvil.Core.Update;

namespace Anvil.App.Tests.Support;

public sealed class FakeElevator : IElevator
{
    public List<ElevatedRequest> Requests { get; } = [];
    public ElevatedResult Next { get; set; } = new(true, null);

    /// <summary>When set, each request's verb is added here too, so tests see it among other fakes' calls.</summary>
    public List<string>? Calls { get; init; }

    /// <summary>Answers for one verb; the others get <see cref="Next"/>.</summary>
    public Dictionary<string, ElevatedResult> ByVerb { get; } = [];

    public Task<ElevatedResult> RunAsync(ElevatedRequest r, CancellationToken ct)
    {
        Requests.Add(r);
        var verb = Verb(r);
        Calls?.Add(verb);
        return Task.FromResult(ByVerb.GetValueOrDefault(verb, Next));
    }

    private static string Verb(ElevatedRequest r) => r switch
    {
        HostsSetRequest => "hosts-set",
        FirewallSetRequest => "firewall-set",
        _ => r.GetType().Name,
    };
}

/// <summary>Records what an update asks of the connection, in <paramref name="calls"/>.</summary>
public sealed class FakeAgentLink(List<string> calls, Func<string>? holdNote = null) : IAgentLink
{
    public Exception? ConnectFails { get; set; }

    public IDisposable HoldStart()
    {
        calls.Add(holdNote is null ? "hold" : $"hold {holdNote()}");
        return new Release(calls);
    }

    public Task WhenAgentGoneAsync(CancellationToken ct)
    {
        calls.Add("gone");
        return Task.CompletedTask;
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        calls.Add("connect");
        return ConnectFails is null ? Task.CompletedTask : Task.FromException(ConnectFails);
    }

    private sealed class Release(List<string> calls) : IDisposable
    {
        public void Dispose() => calls.Add("release");
    }
}

public sealed class FakeRunKey : IRunKey
{
    public string? Command { get; private set; }

    public bool IsSet() => Command is not null;
    public void Set(string command) => Command = command;
    public void Remove() => Command = null;
}

public sealed class FakeFolderPicker : IFolderPicker
{
    public string? Folder { get; set; }
    public string? Program { get; set; }
    public string? File { get; set; }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult(Folder);
    public Task<string?> PickProgramAsync(string title) => Task.FromResult(Program);
    public Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns) => Task.FromResult(File);
}

public sealed class FakeCa : ILocalCaTrust
{
    public bool Trusted { get; set; }
    public int TrustCalls { get; private set; }

    public bool IsTrusted() => Trusted;

    public void Trust()
    {
        TrustCalls++;
        Trusted = true;
    }

    public void Untrust() => Trusted = false;
}

/// <summary>Anvil's own update without GitHub: answers what the test sets.</summary>
public sealed class FakeAppUpdate : IAppUpdate
{
    public List<string> Calls { get; } = [];
    public string? CannotUpdate { get; set; }
    public Release? Available { get; set; }
    public Exception? CheckFails { get; set; }
    public string? InstallProblem { get; set; }

    public Task<Release?> CheckAsync(CancellationToken ct)
    {
        Calls.Add("check");
        return CheckFails is null ? Task.FromResult(Available) : Task.FromException<Release?>(CheckFails);
    }

    public Task<string?> InstallAsync(Release release, IProgress<long>? progress, CancellationToken ct)
    {
        Calls.Add($"install {release.Version}");
        return Task.FromResult(InstallProblem);
    }
}

public sealed class FakeTextClipboard : ITextClipboard
{
    public string? Text { get; private set; }

    public Task SetTextAsync(string text)
    {
        Text = text;
        return Task.CompletedTask;
    }
}
