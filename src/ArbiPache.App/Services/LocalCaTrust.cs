using ArbiPache.Core.Certificates;
using ArbiPache.Core.Paths;

namespace ArbiPache.App.Services;

/// <summary>
/// Trust for the CA the Agent created. The GUI never creates it: data\ belongs to the Agent, so Trust needs a site with a
/// local development certificate first.
/// </summary>
public sealed class LocalCaTrust(AppPaths paths) : ILocalCaTrust
{
    private LocalCa Ca => new(paths, new CertificateStore(paths), TimeProvider.System);

    public bool IsTrusted() => Ca.IsTrusted();

    public void Trust()
    {
        var ca = Ca;
        if (!File.Exists(ca.CertFile))
            throw new InvalidOperationException("There is no local certificate authority yet. Add a site with a local development certificate first.");
        ca.Trust();
    }

    public void Untrust() => Ca.Untrust();
}
