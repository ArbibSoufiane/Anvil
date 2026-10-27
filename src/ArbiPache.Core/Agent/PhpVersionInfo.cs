using ArbiPache.Core.Processes;

namespace ArbiPache.Core.Agent;

/// <summary>One installed PHP version as the PHP page shows it.</summary>
/// <param name="Port">The pool's port while it runs, otherwise 0.</param>
/// <param name="UsedBy">"htdocs" for the default version, then the sites that use it.</param>
public sealed record PhpVersionInfo(string Version, string Toolset, int Workers, int MaxRequests, int Port, IReadOnlyList<string> UsedBy,
    ProcessState State, bool IsDefault, bool EndOfLife);
