namespace ArbiPache.Core.Certificates;

/// <summary>An ACME server (Let's Encrypt) as ArbiPache uses it: one order per certificate, proven over HTTP-01.</summary>
public interface IAcmeProtocol
{
    Task<IAcmeOrder> NewOrderAsync(IReadOnlyList<string> names, CancellationToken ct);
}

public interface IAcmeOrder
{
    /// <summary>For each name: the token the server will fetch at /.well-known/acme-challenge/&lt;token&gt; and the text it expects.</summary>
    IReadOnlyList<(string Token, string KeyAuthorization)> Http01Challenges { get; }

    /// <summary>Asks the server to fetch the challenge files and waits until every name is proven, or throws with the server's reason.</summary>
    Task ValidateAsync(CancellationToken ct);

    /// <summary>Sends a request for a fresh key and returns the issued certificate, its issuers and that key, all PEM.</summary>
    Task<(string CertPem, string ChainPem, string KeyPem)> FinalizeAsync(CancellationToken ct);
}
