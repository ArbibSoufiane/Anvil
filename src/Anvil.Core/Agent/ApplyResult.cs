using Anvil.Core.Elevation;

namespace Anvil.Core.Agent;

/// <summary>
/// Outcome of a change. Error is user-facing; ConfigTestOutput is Apache's own text when a config test ran.
/// HostsDomains and RequiredFirewall name what the GUI must still ask the Elevator to do.
/// </summary>
/// <param name="Elevation">An administrator action this still needs. The window runs it through the Elevator, then, when
/// <paramref name="Ok"/> is false, makes the same call once more.</param>
public sealed record ApplyResult(bool Ok, string? Error, string? ConfigTestOutput, IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string>? HostsDomains = null, FirewallSetRequest? RequiredFirewall = null, ElevatedRequest? Elevation = null)
{
    public static ApplyResult Success(IReadOnlyList<string>? changedFiles = null, string? testOutput = null) =>
        new(true, null, testOutput, changedFiles ?? []);

    public static ApplyResult Failure(string error, string? testOutput = null, IReadOnlyList<string>? changedFiles = null) =>
        new(false, error, testOutput, changedFiles ?? []);
}
