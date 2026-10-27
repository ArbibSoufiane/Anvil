using ArbiPache.App.Services;
using ArbiPache.Core.Elevation;

namespace ArbiPache.App.Tests.Support;

public sealed class FakeElevator : IElevator
{
    public List<ElevatedRequest> Requests { get; } = [];
    public ElevatedResult Next { get; set; } = new(true, null);

    public Task<ElevatedResult> RunAsync(ElevatedRequest r, CancellationToken ct)
    {
        Requests.Add(r);
        return Task.FromResult(Next);
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
