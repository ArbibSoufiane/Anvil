using Anvil.Core.Processes;

namespace Anvil.Core.Php;

/// <summary>The running PHP pools, as the Agent drives them. <see cref="PhpPoolRunner"/> is the real one.</summary>
public interface IPhpPools
{
    IReadOnlyList<PhpPool> Running { get; }

    bool Owns(int port);

    ProcessState StateOf(string key);

    Task ApplyAsync(IReadOnlyList<PhpPool> pools, CancellationToken ct);

    Task RestartAsync(string key, CancellationToken ct);
}
