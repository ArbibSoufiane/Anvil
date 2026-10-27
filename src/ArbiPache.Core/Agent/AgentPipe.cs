namespace ArbiPache.Core.Agent;

public static class AgentPipe
{
    public const string Name = "ArbiPache.Agent";
    public const string Mutex = @"Global\ArbiPache.Agent";
}

/// <param name="RunningAsService">True under the Windows service (Phase 7), false as the GUI's child.</param>
public sealed record AgentOptions(string PipeName, string MutexName, bool RunningAsService);
