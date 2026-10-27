using ArbiPache.App.Services;
using ArbiPache.Core.Elevation;

namespace ArbiPache.App.Tests.Support;

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
        AclGrantRequest => "acl-grant",
        ServiceInstallRequest => "service-install",
        ServiceUninstallRequest => "service-uninstall",
        HostsSetRequest => "hosts-set",
        FirewallSetRequest => "firewall-set",
        _ => r.GetType().Name,
    };
}

/// <summary>Records what the service switch asks of the connection, in <paramref name="calls"/>.</summary>
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

public sealed class FakeAgentService(List<string> calls) : IAgentService
{
    public bool Installed { get; set; }
    public Exception? StartFails { get; set; }

    public bool IsInstalled() => Installed;

    public void Start()
    {
        calls.Add("start");
        if (StartFails is not null) throw StartFails;
    }

    public Task WaitUntilRemovedAsync(CancellationToken ct)
    {
        calls.Add("removed");
        return Task.CompletedTask;
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
