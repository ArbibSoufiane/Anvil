using System.Text.RegularExpressions;
using Anvil.Core.IO;
using Anvil.Core.Paths;
using Anvil.Core.Sites;

namespace Anvil.Core.Certificates;

/// <summary>
/// Gets a Let's Encrypt certificate over HTTP-01: writes each challenge file where Apache serves
/// /.well-known/acme-challenge/ for every port-80 site, lets the server check them, then stores the certificate.
/// </summary>
public sealed partial class AcmeService(Func<bool, IAcmeProtocol> protocolFor, CertificateStore store, AppPaths paths)
{
    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex SafeToken();

    public static string ChallengeDir(AppPaths paths) => Path.Combine(paths.Acme, ".well-known", "acme-challenge");

    public async Task<SiteCertificate> IssueAsync(string siteId, IReadOnlyList<string> names, bool staging, CancellationToken ct)
    {
        var order = await protocolFor(staging).NewOrderAsync(names, ct);
        // Tokens become file names; an ACME server only ever sends base64url text.
        if (order.Http01Challenges.Select(c => c.Token).FirstOrDefault(t => !SafeToken().IsMatch(t)) is { } bad)
            throw new InvalidOperationException($"The certificate authority sent a challenge token Anvil will not write to disk: {bad}");

        var dir = ChallengeDir(paths);
        Directory.CreateDirectory(dir);
        var files = order.Http01Challenges.Select(c => Path.Combine(dir, c.Token)).ToList();
        try
        {
            foreach (var (token, keyAuthorization) in order.Http01Challenges) AtomicFile.WriteAllText(Path.Combine(dir, token), keyAuthorization);
            await order.ValidateAsync(ct);
            var (certPem, chainPem, keyPem) = await order.FinalizeAsync(ct);
            store.Save(siteId, certPem, keyPem, string.IsNullOrWhiteSpace(chainPem) ? null : chainPem, CertSource.LetsEncrypt);
            return store.Get(siteId)!;
        }
        finally
        {
            foreach (var file in files) File.Delete(file);
        }
    }
}
