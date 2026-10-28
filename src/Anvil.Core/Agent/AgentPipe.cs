namespace Anvil.Core.Agent;

public static class AgentPipe
{
    public const string Name = "Anvil.Agent";
    public const string Mutex = @"Global\Anvil.Agent";
}

/// <param name="RunningAsService">True under the Windows service (Phase 7), false as the GUI's child.</param>
public sealed record AgentOptions(string PipeName, string MutexName, bool RunningAsService)
{
    /// <summary><c>--service</c>: started by the Windows service manager; anything else is the GUI's child.</summary>
    public static AgentOptions FromArgs(IReadOnlyList<string> args) => new(AgentPipe.Name, AgentPipe.Mutex, args.Contains("--service"));
}
