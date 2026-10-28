using System.Security.Cryptography;
using Anvil.Core.IO;
using Anvil.Core.Paths;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Directory = System.IO.Directory;

namespace Anvil.Core.Certificates;

/// <summary>
/// Let's Encrypt through Certes. One account per server (staging, production), its key kept in
/// data\certs\acme\account-&lt;staging|production&gt;.pem and created on first use.
/// </summary>
public sealed class CertesAcmeProtocol(AppPaths paths, bool staging, string? email) : IAcmeProtocol
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int MaxPolls = 60;

    public static string AccountKeyFile(AppPaths paths, bool staging) =>
        Path.Combine(paths.Certs, "acme", staging ? "account-staging.pem" : "account-production.pem");

    public static Uri DirectoryFor(bool staging) => staging ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2;

    public async Task<IAcmeOrder> NewOrderAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        var acme = await ContextAsync(ct);
        ct.ThrowIfCancellationRequested();
        var order = await acme.NewOrder(names.ToList());
        var challenges = new List<(string Name, IChallengeContext Challenge)>();
        foreach (var authorization in await order.Authorizations())
        {
            ct.ThrowIfCancellationRequested();
            var resource = await authorization.Resource();
            challenges.Add((resource.Identifier.Value, await authorization.Http()));
        }

        return new Order(order, challenges, names);
    }

    private async Task<AcmeContext> ContextAsync(CancellationToken ct)
    {
        var file = AccountKeyFile(paths, staging);
        if (File.Exists(file)) return new AcmeContext(DirectoryFor(staging), KeyFactory.FromPem(await File.ReadAllTextAsync(file, ct)));

        var key = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var acme = new AcmeContext(DirectoryFor(staging), key);
        if (string.IsNullOrWhiteSpace(email)) await acme.NewAccount(new List<string>(), termsOfServiceAgreed: true);
        else await acme.NewAccount(email, termsOfServiceAgreed: true);
        var dir = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(dir);
        PrivateAcl.RestrictFolder(dir);
        AtomicFile.WriteAllText(file, key.ToPem());
        return acme;
    }

    private sealed class Order(IOrderContext order, IReadOnlyList<(string Name, IChallengeContext Challenge)> challenges, IReadOnlyList<string> names)
        : IAcmeOrder
    {
        public IReadOnlyList<(string Token, string KeyAuthorization)> Http01Challenges { get; } =
            challenges.Select(c => (c.Challenge.Token, c.Challenge.KeyAuthz)).ToList();

        public async Task ValidateAsync(CancellationToken ct)
        {
            foreach (var (_, challenge) in challenges) await challenge.Validate();
            foreach (var (name, challenge) in challenges)
            {
                for (var poll = 0; ; poll++)
                {
                    var resource = await challenge.Resource();
                    if (resource.Status == ChallengeStatus.Valid) break;
                    if (resource.Status == ChallengeStatus.Invalid)
                        throw new InvalidOperationException(
                            $"Let's Encrypt could not verify {name}: {resource.Error?.Detail ?? "it gave no reason"}. " +
                            $"Check that {name} points to this server and port 80 is reachable from the internet.");
                    if (poll == MaxPolls) throw new TimeoutException($"Let's Encrypt did not finish checking {name} within two minutes.");
                    await Task.Delay(PollInterval, ct);
                }
            }
        }

        public async Task<(string CertPem, string ChainPem, string KeyPem)> FinalizeAsync(CancellationToken ct)
        {
            var key = KeyFactory.NewKey(KeyAlgorithm.ES256);
            var chain = await order.Generate(new CsrInfo { CommonName = names[0] }, key);
            ct.ThrowIfCancellationRequested();
            using var pkcs8 = ECDsa.Create();
            pkcs8.ImportFromPem(key.ToPem());
            return (chain.Certificate.ToPem(), string.Join("\n", chain.Issuers.Select(i => i.ToPem())), pkcs8.ExportPkcs8PrivateKeyPem());
        }
    }
}
