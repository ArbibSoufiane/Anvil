namespace Anvil.Core.Redis;

/// <summary>What one run of wsl.exe printed, and its exit code.</summary>
public sealed record WslResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs wsl.exe. Scripts go in on standard input, so nothing in them needs quoting for Windows.</summary>
public interface IWsl
{
    /// <summary>Whether wsl.exe is on this PC at all.</summary>
    bool Exists { get; }

    /// <summary>Runs wsl.exe with <paramref name="args"/>, feeding <paramref name="stdin"/> when given; each line printed goes to <paramref name="onLine"/>.</summary>
    Task<WslResult> RunAsync(IReadOnlyList<string> args, string? stdin, Action<string>? onLine, TimeSpan timeout, CancellationToken ct);

    /// <summary>Starts wsl.exe and leaves it running, without waiting; it outlives Anvil's window.</summary>
    void StartDetached(IReadOnlyList<string> args, string stdin);
}
