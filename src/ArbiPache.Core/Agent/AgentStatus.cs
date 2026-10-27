using ArbiPache.Core.Preflight;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;

namespace ArbiPache.Core.Agent;

public enum ServiceId { Apache, Php, Database }

/// <summary>Detail is the last error when the service failed, otherwise a short description.</summary>
public sealed record ServiceStatus(ServiceId Id, ProcessState State, string? Version, string? Detail);

public sealed record AgentStatus(ServerMode Mode, IReadOnlyList<ServiceStatus> Services, IReadOnlyList<PreflightFinding> Findings,
    string AgentVersion, bool RunningAsService);
