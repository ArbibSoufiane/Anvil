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

/// <summary>
/// Replaces every firewall rule for Anvil's programs with the planned ones: <paramref name="ApacheVersion"/>'s httpd.exe on
/// these TCP ports, and the Agent on UDP 53 when <paramref name="NameService"/>. See <see cref="FirewallRulePlanner"/>.
/// </summary>
public sealed record FirewallSetRequest(string? ApacheVersion, IReadOnlyList<int> Ports, bool NameService) : ElevatedRequest
{
    // A record compares lists by reference; two requests for the same ports are the same request.
    public bool Equals(FirewallSetRequest? other) =>
        other is not null && ApacheVersion == other.ApacheVersion && NameService == other.NameService
        && (Ports ?? []).SequenceEqual(other.Ports ?? []);

    public override int GetHashCode() => HashCode.Combine(ApacheVersion, NameService, Ports?.Count ?? 0);
}

public sealed record ElevatedResult(bool Ok, string? Error);

public static class ElevatedJson
{
    public static string Serialize(ElevatedRequest r) => JsonSerializer.Serialize(r, JsonFiles.Options);

    public static string Serialize(ElevatedResult r) => JsonSerializer.Serialize(r, JsonFiles.Options);

    /// <summary>Throws JsonException or NotSupportedException for malformed JSON or an unknown verb.</summary>
    public static ElevatedRequest? Deserialize(string json) => JsonSerializer.Deserialize<ElevatedRequest>(json, JsonFiles.Options);
}
