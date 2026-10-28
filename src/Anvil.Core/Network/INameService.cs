using System.Net;

namespace Anvil.Core.Network;

/// <summary>An address the name service could not listen on, with the reason Windows gave.</summary>
public sealed record DnsBindFailure(IPAddress Address, string Reason);

/// <summary>Answers DNS queries for the network domain on this PC's LAN addresses.</summary>
public interface INameService
{
    /// <summary>
    /// Listens on exactly these addresses for <paramref name="networkDomain"/>; a null domain or no addresses stops it.
    /// </summary>
    /// <returns>The addresses it could not listen on.</returns>
    Task<IReadOnlyList<DnsBindFailure>> ApplyAsync(string? networkDomain, IReadOnlyList<IPAddress> addresses, CancellationToken ct);

    IReadOnlyList<IPAddress> Listening { get; }
}
