using Anvil.Core.Apache;
using Anvil.Core.Certificates;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Modes;
using Anvil.Core.Php;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using StreamJsonRpc;

namespace Anvil.Core.Agent;

/// <summary>What the GUI may ask of the Agent, over the named pipe <see cref="AgentPipe.Name"/>.</summary>
[JsonRpcContract]
public partial interface IAgentApi
{
    event EventHandler<AgentStatus> StatusChanged;

    Task<AgentStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>A null id means every service.</summary>
    Task<ApplyResult> StartAsync(ServiceId? id, CancellationToken ct = default);

    Task<ApplyResult> StopAsync(ServiceId? id, CancellationToken ct = default);
    Task<ApplyResult> RestartAsync(ServiceId? id, CancellationToken ct = default);
    Task<ConfigTestResult> TestConfigAsync(CancellationToken ct = default);
    Task<AppSettings> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Settings are kept only when the configuration they produce passes Apache's test.</summary>
    Task<ApplyResult> SaveSettingsAsync(AppSettings s, CancellationToken ct = default);

    Task<IReadOnlyList<ComponentRelease>> GetAvailableAsync(ComponentKind k, CancellationToken ct = default);
    Task<IReadOnlyList<InstalledComponent>> GetInstalledAsync(ComponentKind k, CancellationToken ct = default);
    Task<InstalledComponent> InstallAsync(ComponentRelease r, CancellationToken ct = default);

    /// <summary>
    /// Switches to an installed version. Apache must pass the config test and answer; PHP moves everything on the
    /// version's branch to it; the database takes a newer release of its series. A version that does not run is put
    /// back, and the error says so.
    /// </summary>
    Task<ApplyResult> ActivateAsync(ComponentKind k, string version, CancellationToken ct = default);

    /// <summary>Newer patch releases of the active Apache, each PHP branch in use and the active database series.</summary>
    Task<IReadOnlyList<UpdateOffer>> CheckUpdatesAsync(CancellationToken ct = default);

    /// <summary>Installed PHP versions, newest first.</summary>
    Task<IReadOnlyList<PhpVersionInfo>> GetPhpAsync(CancellationToken ct = default);

    Task<ApplyResult> SetPhpPoolAsync(string version, PhpPoolSettings settings, CancellationToken ct = default);
    Task<ApplyResult> SetDefaultPhpAsync(string version, CancellationToken ct = default);
    Task<IReadOnlyList<PhpExtension>> GetExtensionsAsync(string version, CancellationToken ct = default);

    /// <summary>Restarts that version's pool only.</summary>
    Task<ApplyResult> SetExtensionAsync(string version, string name, bool enabled, CancellationToken ct = default);

    Task<IReadOnlyList<SiteDefinition>> GetSitesAsync(CancellationToken ct = default);

    /// <summary>What localhost/&lt;folder&gt; serves: htdocs folders and htdocs entries kept elsewhere.</summary>
    Task<IReadOnlyList<HtdocsEntry>> GetHtdocsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ValidationError>> ValidateSiteAsync(SiteDefinition s, CancellationToken ct = default);

    /// <summary>
    /// Kept only when Apache accepts the result. HostsDomains in the result, when set, is the list the GUI must hand to
    /// the Elevator. Secrets are needed the first time a site is password protected.
    /// </summary>
    Task<ApplyResult> SaveSiteAsync(SiteDefinition s, SiteSecrets? secrets, CancellationToken ct = default);

    Task<ApplyResult> RemoveSiteAsync(string id, CancellationToken ct = default);
    Task<ApplyResult> SetSiteEnabledAsync(string id, bool enabled, CancellationToken ct = default);
    Task<Detected> DetectAsync(string folder, CancellationToken ct = default);

    /// <summary>Refuses a folder name htdocs already has.</summary>
    Task<ApplyResult> CopyIntoHtdocsAsync(string sourceFolder, string folderName, CancellationToken ct = default);

    /// <summary>Brings in ArbiPache 3's sites. Result.HostsDomains, when set, is the list to hand to the Elevator.</summary>
    Task<ImportReport> ImportLegacyAsync(CancellationToken ct = default);

    /// <summary>Issues every local development certificate again from the local CA and reloads Apache.</summary>
    Task<ApplyResult> ReissueLocalCertificatesAsync(CancellationToken ct = default);

    /// <summary>Each site's certificate on disk, by site id.</summary>
    Task<IReadOnlyDictionary<string, SiteCertificate>> GetCertificatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks the client's certificate against the saved site, stores it and reloads Apache. A certificate problem fails
    /// with a <see cref="CertificateImportException"/> whose code <see cref="AgentErrors.CertificateCode"/> reads on the GUI side.
    /// </summary>
    Task<SiteCertificate> ImportCertificateAsync(string siteId, CertImportRequest r, CancellationToken ct = default);

    /// <summary>Orders the saved site's certificate from Let's Encrypt over HTTP-01 and reloads Apache.</summary>
    Task<SiteCertificate> IssueLetsEncryptAsync(string siteId, bool staging, CancellationToken ct = default);

    /// <summary>The addresses a domain resolves to from this server; empty when it does not resolve.</summary>
    Task<IReadOnlyList<string>> ResolveAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Makes an installed MariaDB or MySQL version the active database: initializes an empty datadir with the root
    /// password (an existing one must accept it), stops the other engine and starts this one.
    /// </summary>
    Task<ApplyResult> InstallDatabaseAsync(DbEngine e, string version, string rootPassword, CancellationToken ct = default);

    Task<IReadOnlyList<DbSettingRow>> GetDbSettingsAsync(CancellationToken ct = default);

    /// <summary>Writes my.ini (a null value removes the key) and restarts the server; put back when it will not start.</summary>
    Task<ApplyResult> SaveDbSettingsAsync(IReadOnlyDictionary<string, string?> changes, CancellationToken ct = default);

    Task<ApplyResult> SetDbRootPasswordAsync(string password, CancellationToken ct = default);

    /// <summary>Dumps every database to data\backups\db; returns the file.</summary>
    Task<string> BackupDatabaseAsync(CancellationToken ct = default);

    /// <summary>What switching to the mode would change and restart. Writes nothing.</summary>
    Task<ModeChangeSet> PreviewModeAsync(ServerMode target, CancellationToken ct = default);

    /// <summary>
    /// Backs up the files, switches to the mode and restarts what changed. Any failure puts the previous mode and its
    /// files back. RequiredFirewall names the scope the web ports need in the new mode.
    /// </summary>
    Task<ApplyResult> ApplyModeAsync(ServerMode target, ModeOptions options, CancellationToken ct = default);

    /// <summary>Development, then Production: the built-in values with the user's edits.</summary>
    Task<IReadOnlyList<ModeProfile>> GetModeProfilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores the user's edits to a profile (replacing earlier ones). For the active mode it also rewrites the mode's files
    /// and restarts what changed, with the same backup and rollback as a switch.
    /// </summary>
    Task<ApplyResult> SaveModeProfileAsync(ServerMode mode, ModeProfileOverrides overrides, CancellationToken ct = default);

    /// <summary>Ends the Agent. With stopServices false, its processes still end with it (they live in its job).</summary>
    Task ShutdownAsync(bool stopServices, CancellationToken ct = default);
}
