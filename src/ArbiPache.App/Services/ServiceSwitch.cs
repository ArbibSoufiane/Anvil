using ArbiPache.Core.Agent;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.Paths;
using Serilog;

namespace ArbiPache.App.Services;

/// <summary>The parts of <see cref="AgentConnection"/> the service switch drives.</summary>
public interface IAgentLink
{
    /// <summary>Starts no Agent until disposed, so the switch decides which Agent runs next.</summary>
    IDisposable HoldStart();

    /// <summary>Completes once no Agent process runs any more.</summary>
    Task WhenAgentGoneAsync(CancellationToken ct);

    /// <summary>Connects again, starting the Agent (the service when installed, else the child) if nobody answers.</summary>
    Task ConnectAsync(CancellationToken ct);
}

/// <summary>The ArbiPacheAgent Windows service as the signed-in user sees it.</summary>
public interface IAgentService
{
    bool IsInstalled();

    /// <summary>Asks Windows to start the service; the installing user may.</summary>
    void Start();

    /// <summary>Completes once Windows has removed the service, which happens when it has stopped.</summary>
    Task WaitUntilRemovedAsync(CancellationToken ct);
}

/// <summary>
/// Moves the Agent between the window's child process and the ArbiPacheAgent Windows service. Both ask Windows through
/// the Elevator; whatever is refused comes back as a sentence, with the Agent put back the way it was.
/// </summary>
public sealed class ServiceSwitch(IAgentApi api, IAgentLink link, IElevator elevator, IAgentService service, AppPaths paths, string userSid)
{
    private const string AsBefore = "ArbiPache keeps running as before, without the service.";

    /// <returns>Null when the Agent now runs as the service.</returns>
    public async Task<string?> TurnOnAsync(CancellationToken ct = default)
    {
        var before = await api.GetSettingsAsync(ct);
        var sites = await api.GetSitesAsync(ct);
        // The service reads who may use its pipe when it starts, so the user is on the list before it does.
        var saved = await api.SaveSettingsAsync(before with { RunAsService = true, ControlUserSids = [userSid] }, ct);
        if (!saved.Ok) return saved.Error;

        string? problem;
        using (link.HoldStart())
        {
            await StopAgentAsync(ct);
            problem = await ElevateAsync(new AclGrantRequest(AclPlanner.Plan(paths, sites, before.HtdocsPath)), "give its service access to the site folders", ct)
                      ?? await ElevateAsync(new ServiceInstallRequest(Path.Combine(paths.Root, "ArbiPache.Agent.exe"), userSid), "install its service", ct);
            if (problem is null)
            {
                try
                {
                    service.Start();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.Warning(e, "The ArbiPache service did not start");
                    problem = $"The ArbiPache service did not start ({e.Message}).";
                    if (await ElevateAsync(new ServiceUninstallRequest(), "remove its service again", ct) is { } stuck) problem += " " + stuck;
                    else await service.WaitUntilRemovedAsync(ct);
                }
            }
        }

        try
        {
            await link.ConnectAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException && problem is null)
        {
            Log.Warning(e, "The window cannot reach the ArbiPache service");
            return $"ArbiPache now runs as a Windows service, but this window cannot reach it yet ({e.Message}). It keeps trying.";
        }

        if (problem is null) return null;
        await api.SaveSettingsAsync(before, ct);
        return $"{problem} {AsBefore}";
    }

    /// <returns>Null when the Agent runs as the window's child again.</returns>
    public async Task<string?> TurnOffAsync(CancellationToken ct = default)
    {
        using (link.HoldStart())
        {
            if (await ElevateAsync(new ServiceUninstallRequest(), "remove its service", ct) is { } problem)
                return $"{problem} ArbiPache keeps running as a Windows service.";
            await link.WhenAgentGoneAsync(ct);
            await service.WaitUntilRemovedAsync(ct);
        }

        await link.ConnectAsync(ct);
        var settings = await api.GetSettingsAsync(ct);
        var saved = await api.SaveSettingsAsync(settings with { RunAsService = false }, ct);
        return saved.Ok ? null : saved.Error;
    }

    private async Task StopAgentAsync(CancellationToken ct)
    {
        try
        {
            await api.ShutdownAsync(stopServices: true, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The pipe closes as the Agent exits.
            Log.Information("The Agent closed the connection while shutting down: {Message}", e.Message);
        }
        await link.WhenAgentGoneAsync(ct);
    }

    private async Task<string?> ElevateAsync(ElevatedRequest request, string what, CancellationToken ct)
    {
        string? error;
        try
        {
            var result = await elevator.RunAsync(request, ct);
            error = result.Ok ? null : result.Error ?? "no reason given";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "The Elevator failed");
            error = e.Message;
        }
        return error is null ? null : $"Windows did not let ArbiPache {what} ({error}).";
    }
}
