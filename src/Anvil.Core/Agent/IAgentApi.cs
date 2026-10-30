using Anvil.Core.Apache;
using Anvil.Core.Certificates;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Elevation;
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

    /// <summary>The PHP versions pvm has on this PC, newest first, each saying whether Anvil has that version.</summary>
    Task<IReadOnlyList<PhpFound>> FindPvmPhpAsync(CancellationToken ct = default);

    /// <summary>Adds the PHP in a folder Anvil did not download, such as pvm's. It becomes the default when there is none.</summary>
    Task<ApplyResult> LinkPhpAsync(string folder, CancellationToken ct = default);

    /// <summary>Removes a PHP added from a folder; the folder stays. Refused while it is the default or a site uses it.</summary>
    Task<ApplyResult> UnlinkPhpAsync(string version, CancellationToken ct = default);

    Task<ApplyResult> SetPhpPoolAsync(string version, PhpPoolSettings settings, CancellationToken ct = default);
    Task<ApplyResult> SetDefaultPhpAsync(string version, CancellationToken ct = default);
    Task<IReadOnlyList<PhpExtension>> GetExtensionsAsync(string version, CancellationToken ct = default);

    /// <summary>Restarts that version's pool only.</summary>
    Task<ApplyResult> SetExtensionAsync(string version, string name, bool enabled, CancellationToken ct = default);

    /// <summary>The php.ini values the PHP page edits, and whether OPcache is on.</summary>
    Task<PhpIniValues> GetPhpIniAsync(string version, CancellationToken ct = default);

    /// <summary>Writes the values into that version's php.ini and restarts its pool; a value PHP would not read is refused.</summary>
    Task<ApplyResult> SavePhpIniAsync(string version, IReadOnlyDictionary<string, string> values, CancellationToken ct = default);

    /// <summary>Loads OPcache and turns it on, or off, in that version's php.ini; restarts its pool.</summary>
    Task<ApplyResult> SetOpcacheAsync(string version, bool enabled, CancellationToken ct = default);

    Task<IReadOnlyList<SiteDefinition>> GetSitesAsync(CancellationToken ct = default);

    /// <summary>What localhost/&lt;folder&gt; serves: htdocs folders and htdocs entries kept elsewhere.</summary>
    Task<IReadOnlyList<HtdocsEntry>> GetHtdocsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ValidationError>> ValidateSiteAsync(SiteDefinition s, CancellationToken ct = default);

    /// <summary>
    /// Kept only when Apache accepts the result. HostsDomains in the result, when set, is the list the GUI must hand to
    /// the Elevator.
    /// </summary>
    Task<ApplyResult> SaveSiteAsync(SiteDefinition s, CancellationToken ct = default);

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

    /// <summary>The firewall rules to ask Windows for while something is open to the network; null when nothing is.</summary>
    Task<FirewallSetRequest?> GetFirewallRequestAsync(CancellationToken ct = default);

    /// <summary>Reads this PC's addresses, its networks and the firewall again, and moves the name service to match.</summary>
    Task RefreshNetworkAsync(CancellationToken ct = default);

    /// <summary>The MariaDB and MySQL servers the user linked, with their state.</summary>
    Task<IReadOnlyList<LinkedDbInfo>> GetLinkedDatabasesAsync(CancellationToken ct = default);

    /// <summary>
    /// Links the server in a bin folder, with the account Anvil logs in with. A result's Elevation, when set, asks Windows to
    /// let the user start and stop its service.
    /// </summary>
    Task<ApplyResult> LinkDatabaseAsync(string binDir, string user, string password, CancellationToken ct = default);

    /// <summary>Forgets a linked server. A result's Elevation takes back the right to start and stop its service.</summary>
    Task<ApplyResult> UnlinkDatabaseAsync(string id, CancellationToken ct = default);

    Task<ApplyResult> StartLinkedDatabaseAsync(string id, CancellationToken ct = default);
    Task<ApplyResult> StopLinkedDatabaseAsync(string id, CancellationToken ct = default);

    /// <summary>A failed result with an Elevation needs that run first, then the same call again.</summary>
    Task<ApplyResult> SetLinkedDatabasePortAsync(string id, int port, CancellationToken ct = default);

    /// <summary>Dumps every database of a running linked server to data\backups\db\&lt;id&gt;; returns the file.</summary>
    Task<string> BackupLinkedDatabaseAsync(string id, CancellationToken ct = default);

    /// <summary>Whether a linked server still runs and lets Anvil log in; a failed result says what is wrong.</summary>
    Task<ApplyResult> CheckLinkedDatabaseAsync(string id, CancellationToken ct = default);

    /// <summary>Ends the Agent. With stopServices false, its processes still end with it (they live in its job).</summary>
    Task ShutdownAsync(bool stopServices, CancellationToken ct = default);
}
