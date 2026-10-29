using Anvil.Core.Processes;

namespace Anvil.Core.Agent;

/// <summary>One installed PHP version as the PHP page shows it.</summary>
/// <param name="Port">The pool's port while it runs, otherwise 0.</param>
/// <param name="UsedBy">"htdocs" for the default version, then the sites that use it.</param>
/// <param name="Folder">Where its files are: under runtime\php, or a linked folder such as one of pvm's.</param>
/// <param name="Linked">Added from a folder Anvil did not download; removing it from Anvil leaves the folder alone.</param>
public sealed record PhpVersionInfo(string Version, string Toolset, int Workers, int MaxRequests, int Port, IReadOnlyList<string> UsedBy,
    ProcessState State, bool IsDefault, bool EndOfLife, string Folder = "", bool Linked = false, bool ThreadSafe = false);

/// <summary>A PHP version found on this PC, such as one of pvm's, that Anvil can add.</summary>
/// <param name="InAnvil">Anvil already has this version, downloaded or added.</param>
public sealed record PhpFound(string Version, string Folder, string Toolset, bool ThreadSafe, bool InAnvil);
