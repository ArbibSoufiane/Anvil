using System.Text.Json;
using System.Text.Json.Serialization;
using ArbiPache.Core.IO;

namespace ArbiPache.Core.Elevation;

/// <summary>One administrator action, handed to ArbiPache.Elevator.exe as JSON. The verb set is fixed.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "verb")]
[JsonDerivedType(typeof(HostsSetRequest), "hosts-set")]
[JsonDerivedType(typeof(FirewallSetRequest), "firewall-set")]
[JsonDerivedType(typeof(ServiceInstallRequest), "service-install")]
[JsonDerivedType(typeof(ServiceUninstallRequest), "service-uninstall")]
[JsonDerivedType(typeof(AclGrantRequest), "acl-grant")]
public abstract record ElevatedRequest;

/// <summary>Makes ArbiPache's hosts block map exactly these domains to this PC.</summary>
public sealed record HostsSetRequest(IReadOnlyList<string> Domains) : ElevatedRequest;

/// <summary>Makes ArbiPache's firewall rules open exactly these ports to <paramref name="Scope"/>. No ports closes them all.</summary>
public sealed record FirewallSetRequest(IReadOnlyList<int> Ports, FirewallScope Scope) : ElevatedRequest;

/// <summary>
/// Installs the ArbiPacheAgent service running <paramref name="AgentExe"/> --service as NT SERVICE\ArbiPacheAgent.
/// <paramref name="ControlUserSid"/> may start and stop it.
/// </summary>
public sealed record ServiceInstallRequest(string AgentExe, string ControlUserSid) : ElevatedRequest;

/// <summary>Stops and removes the ArbiPacheAgent service.</summary>
public sealed record ServiceUninstallRequest() : ElevatedRequest;

/// <summary>Gives the service account these rights, inherited by everything below each folder.</summary>
public sealed record AclGrantRequest(IReadOnlyList<AclGrant> Grants) : ElevatedRequest;

public sealed record ElevatedResult(bool Ok, string? Error);

public static class ElevatedJson
{
    public static string Serialize(ElevatedRequest r) => JsonSerializer.Serialize(r, JsonFiles.Options);

    public static string Serialize(ElevatedResult r) => JsonSerializer.Serialize(r, JsonFiles.Options);

    /// <summary>Throws JsonException or NotSupportedException for malformed JSON or an unknown verb.</summary>
    public static ElevatedRequest? Deserialize(string json) => JsonSerializer.Deserialize<ElevatedRequest>(json, JsonFiles.Options);
}
