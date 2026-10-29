using Anvil.Core.Database;
using Anvil.Core.Processes;

namespace Anvil.Core.Agent;

/// <summary>A server the user linked, as the Database page shows it.</summary>
/// <param name="Service">The Windows service that runs it; null when Anvil runs it.</param>
/// <param name="CanControl">Start and Stop work without an administrator prompt.</param>
public sealed record LinkedDbInfo(string Id, DbEngine Engine, string Version, string BinDir, string? Service, int Port, string User,
    ProcessState State, bool CanControl);
