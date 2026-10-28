using Anvil.Core.Preflight;
using Anvil.Core.Processes;

namespace Anvil.Core.Agent;

public enum ServiceId { Apache, Php, Database }

/// <summary>Detail is the last error when the service failed, otherwise a short description.</summary>
public sealed record ServiceStatus(ServiceId Id, ProcessState State, string? Version, string? Detail);

public sealed record AgentStatus(IReadOnlyList<ServiceStatus> Services, IReadOnlyList<PreflightFinding> Findings, string AgentVersion);
