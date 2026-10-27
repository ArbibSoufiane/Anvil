using System.ServiceProcess;
using ArbiPache.Core.Elevation;

namespace ArbiPache.App.Services;

/// <summary>The ArbiPacheAgent service through the Windows service manager; its permissions let the installing user start it.</summary>
public sealed class WindowsAgentService : IAgentService
{
    private static readonly TimeSpan RemoveWithin = TimeSpan.FromMinutes(1);

    public bool IsInstalled()
    {
        using var controller = new ServiceController(AclPlanner.ServiceName);
        try
        {
            _ = controller.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Start()
    {
        using var controller = new ServiceController(AclPlanner.ServiceName);
        if (controller.Status == ServiceControllerStatus.Stopped) controller.Start();
    }

    public async Task WaitUntilRemovedAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + RemoveWithin;
        while (IsInstalled())
        {
            if (DateTime.UtcNow > deadline) throw new System.TimeoutException("Windows has not removed the ArbiPache service within a minute.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }
}
