namespace Anvil.Core.Agent;

public static class AgentPipe
{
    public const string Name = "Anvil.Agent";
    public const string Mutex = @"Global\Anvil.Agent";
}

/// <summary>The Agent always runs as the window's child.</summary>
public sealed record AgentOptions(string PipeName, string MutexName)
{
    public static AgentOptions FromArgs(IReadOnlyList<string> args) => new(AgentPipe.Name, AgentPipe.Mutex);
}
