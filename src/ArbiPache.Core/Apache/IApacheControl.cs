using ArbiPache.Core.Processes;

namespace ArbiPache.Core.Apache;

/// <summary>The Apache process as the Agent's orchestrator sees it.</summary>
public interface IApacheControl
{
    /// <summary>Null until Apache was started once.</summary>
    ProcessStatus? Status { get; }

    event EventHandler<ProcessStatus>? StatusChanged;

    Task StartAsync(string version, int httpPort, CancellationToken ct);
    Task GracefulRestartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
