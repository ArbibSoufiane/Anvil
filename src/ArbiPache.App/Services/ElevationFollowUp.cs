using ArbiPache.Core.Agent;
using ArbiPache.Core.Elevation;
using Serilog;

namespace ArbiPache.App.Services;

/// <summary>
/// Runs what an Agent result still needs from an administrator: the hosts file, the service's access to site folders and
/// the firewall rules. The change itself is already saved; each step Windows refuses comes back as a sentence for the user.
/// </summary>
public sealed class ElevationFollowUp(IElevator elevator)
{
    /// <returns>Null when nothing was refused.</returns>
    public async Task<string?> RunAsync(ApplyResult result, int httpPort, int httpsPort, CancellationToken ct = default)
    {
        var problems = new List<string>();
        if (result.HostsDomains is { } domains && await TryAsync(new HostsSetRequest(domains), ct) is { } error)
            problems.Add(domains.Count == 0
                ? $"Windows did not let ArbiPache update the hosts file ({error}). Old entries stay there until it does."
                : $"Windows did not let ArbiPache update the hosts file ({error}). {string.Join(", ", domains)} will not open in the browser "
                  + "until it does: save the site again to retry.");
        if (result.RequiredGrants is { Count: > 0 } grants && await TryAsync(new AclGrantRequest(grants), ct) is { } denied)
        {
            var folders = grants.Select(g => g.Path).ToList();
            var list = folders.Count == 1 ? folders[0] : $"{string.Join(", ", folders[..^1])} and {folders[^1]}";
            problems.Add($"Windows did not let ArbiPache give its service access to {list} ({denied}). Those sites show an error until it does: "
                         + "save the site again to retry.");
        }
        if (result.RequiredFirewall is { } scope && await FirewallAsync(scope, httpPort, httpsPort, ct) is { } firewall)
            problems.Add(firewall);
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    /// <returns>Null when the rules are in place.</returns>
    public async Task<string?> FirewallAsync(FirewallScope scope, int httpPort, int httpsPort, CancellationToken ct = default) =>
        await TryAsync(new FirewallSetRequest([httpPort, httpsPort], scope), ct) is { } error
            ? $"Windows did not let ArbiPache open ports {httpPort} and {httpsPort} in the firewall ({error}). Other devices cannot reach this PC until it does."
            : null;

    private async Task<string?> TryAsync(ElevatedRequest request, CancellationToken ct)
    {
        try
        {
            var r = await elevator.RunAsync(request, ct);
            return r.Ok ? null : r.Error ?? "no reason given";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "The Elevator failed");
            return e.Message;
        }
    }
}
