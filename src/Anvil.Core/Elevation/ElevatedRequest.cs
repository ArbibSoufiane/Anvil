using System.Text.Json;
using System.Text.Json.Serialization;
using Anvil.Core.IO;

namespace Anvil.Core.Elevation;

/// <summary>One administrator action, handed to Anvil.Elevator.exe as JSON. The verb set is fixed.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "verb")]
[JsonDerivedType(typeof(HostsSetRequest), "hosts-set")]
[JsonDerivedType(typeof(FirewallSetRequest), "firewall-set")]
[JsonDerivedType(typeof(DbServiceAccessRequest), "db-service-access")]
[JsonDerivedType(typeof(DbPortRequest), "db-port")]
public abstract record ElevatedRequest
{
    /// <summary>The verbs above, the only ones the Elevator runs.</summary>
    public static IReadOnlySet<string> Verbs { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "hosts-set", "firewall-set", "db-service-access", "db-port" };
}

/// <summary>
/// Lets the user who wrote the request start and stop one MariaDB or MySQL service without asking again, or takes that
/// back when <paramref name="Grant"/> is false.
/// </summary>
public sealed record DbServiceAccessRequest(string Service, bool Grant) : ElevatedRequest;

/// <summary>Sets the port in the settings file a MariaDB or MySQL service's command line names.</summary>
public sealed record DbPortRequest(string Service, int Port) : ElevatedRequest;

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
