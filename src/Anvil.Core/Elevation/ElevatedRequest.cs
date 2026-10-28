using System.Text.Json;
using System.Text.Json.Serialization;
using Anvil.Core.IO;

namespace Anvil.Core.Elevation;

/// <summary>One administrator action, handed to Anvil.Elevator.exe as JSON. The verb set is fixed.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "verb")]
[JsonDerivedType(typeof(HostsSetRequest), "hosts-set")]
[JsonDerivedType(typeof(FirewallSetRequest), "firewall-set")]
public abstract record ElevatedRequest
{
    /// <summary>The verbs above, the only ones the Elevator runs.</summary>
    public static IReadOnlySet<string> Verbs { get; } = new HashSet<string>(StringComparer.Ordinal) { "hosts-set", "firewall-set" };
}

/// <summary>Makes Anvil's hosts block map exactly these domains to this PC.</summary>
public sealed record HostsSetRequest(IReadOnlyList<string> Domains) : ElevatedRequest;

/// <summary>Makes Anvil's firewall rules open exactly these ports to <paramref name="Scope"/>. No ports closes them all.</summary>
public sealed record FirewallSetRequest(IReadOnlyList<int> Ports, FirewallScope Scope) : ElevatedRequest;

public sealed record ElevatedResult(bool Ok, string? Error);

public static class ElevatedJson
{
    public static string Serialize(ElevatedRequest r) => JsonSerializer.Serialize(r, JsonFiles.Options);

    public static string Serialize(ElevatedResult r) => JsonSerializer.Serialize(r, JsonFiles.Options);

    /// <summary>Throws JsonException or NotSupportedException for malformed JSON or an unknown verb.</summary>
    public static ElevatedRequest? Deserialize(string json) => JsonSerializer.Deserialize<ElevatedRequest>(json, JsonFiles.Options);
}
