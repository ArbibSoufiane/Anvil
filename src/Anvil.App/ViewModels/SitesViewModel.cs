using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Anvil.App.Services;
using Anvil.Core.Agent;
using Anvil.Core.Certificates;
using Anvil.Core.Elevation;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Anvil.App.ViewModels;

/// <summary>One domain or IP site in the sites table.</summary>
public sealed partial class SiteRow : ObservableObject
{
    public required SiteDefinition Site { get; init; }
    public required string Php { get; init; }
    public required string Url { get; init; }

    /// <summary>The certificate on disk for this site, when there is one.</summary>

    public string Name => Site.Name;

    public string Detail => Site.Kind == SiteKind.Ip
        ? string.Join(" · ", new[] { "IP site", "single site mode" }.Concat(Site.Mounts.Select(m => m.UrlPath)))
        : string.Join(", ", Site.Aliases);

    public string TypeText => Site.Type switch
    {
        SiteType.Static => "Static",
        SiteType.Proxy => "Proxy",
        _ => "PHP",
    };

    public string Folder => Site.Root;

    public string Certificate => Site.Cert switch
    {
        CertSource.LoadBalancer => "HTTP only · client load balancer",
        CertSource.LocalDev => "Local development",
        _ => "HTTP only",
    };

    public string AccessText => SitesViewModel.AccessLabel(Site.Access);
    public bool IsNetwork => Site.Access == AccessLevel.Network;
    public bool IsRestricted => Site.Access is AccessLevel.ThisPc or AccessLevel.SpecificIps;
    public bool HasDetail => Detail.Length > 0;
    public bool IsEnabled => Site.Enabled;
    public string StatusText => Site.Enabled ? "Enabled" : "Disabled";
    public string ToggleText => Site.Enabled ? "Disable" : "Enable";

    [ObservableProperty]
    public partial bool ConfirmingRemove { get; set; }
}

/// <summary>One folder answering at localhost/&lt;folder&gt;.</summary>
public sealed record HtdocsRow(HtdocsEntry Entry, string Local, string Network, string Php)
{
    public string Folder => Entry.Folder;
    public string? Detail => Entry.IsAlias ? $"kept in {Entry.Root}" : null;
}

/// <summary>
/// The Sites page: domain and IP sites, the htdocs folders, and the add/edit form. After the Agent keeps a change, the
/// hosts file and firewall follow through UAC; a refused prompt leaves the site saved and says what is missing.
/// </summary>
public sealed partial class SitesViewModel : ObservableObject
{
    private readonly IAgentApi _api;
    private readonly ILocalCaTrust _ca;
    private readonly IFolderPicker _picker;
    private readonly AppPaths _paths;
    private readonly Func<IReadOnlyList<string>> _addresses;
    private readonly Func<string?> _lanIp;
    private readonly ElevationFollowUp _followUp;
    private readonly Action<Action> _ui;
    private AppSettings _settings = new();
    private IReadOnlyList<PhpVersionInfo> _php = [];

    public SitesViewModel(IAgentApi api, IElevator elevator, ILocalCaTrust ca, IFolderPicker picker, AppPaths paths,
        Func<IReadOnlyList<string>> addresses, Func<string?> lanIp, Action<Action>? ui = null)
    {
        _api = api;
        _ca = ca;
        _picker = picker;
        _paths = paths;
        _addresses = addresses;
        _lanIp = lanIp;
        _followUp = new ElevationFollowUp(elevator);
        _ui = ui ?? (a => Dispatcher.UIThread.Post(a));
    }

    public ObservableCollection<SiteRow> Sites { get; } = [];
    public ObservableCollection<SiteRow> VisibleSites { get; } = [];
    public ObservableCollection<HtdocsRow> Htdocs { get; } = [];

    public string SitesTitle => $"Sites · {Sites.Count}";
    public bool HasSites => Sites.Count > 0;
    public string HtdocsTitle => $"htdocs folders · {Htdocs.Count}";
    public string HtdocsPath => _settings.HtdocsPath ?? _paths.Htdocs;
    public string LocalPattern => $"{Host("localhost")}/<folder>";
    public string NetworkPattern => _lanIp() is { } lan ? $"{Host(lan)}/<folder>" : "this PC's IP/<folder>";

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor))]
    public partial AddSiteViewModel? Editor { get; set; }

    public bool HasEditor => Editor is not null;

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool MessageOk { get; set; }

    /// <summary>Shown after the first local development site while Windows does not trust Anvil's CA yet.</summary>
    [ObservableProperty]
    public partial bool OfferTrust { get; set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            _settings = await _api.GetSettingsAsync(ct);
            _php = await _api.GetPhpAsync(ct);
            var sites = await _api.GetSitesAsync(ct);
            var htdocs = await _api.GetHtdocsAsync(ct);
            _ui(() => Fill(sites, htdocs));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Loading the sites page failed");
            _ui(() => Say($"The Anvil Agent did not answer: {e.Message}", ok: false));
        }
    }

    internal static string AccessLabel(AccessLevel access) => access switch
    {
        AccessLevel.Everyone => "Everyone",
        AccessLevel.Network => "My network only",
        AccessLevel.ThisPc => "This PC only",
        _ => "Specific IPs",
    };

    partial void OnSearchTextChanged(string value) => Filter();

    [RelayCommand]
    private async Task AddAsync()
    {
        await RefreshPhpAsync();
        Editor = NewEditor(null);
    }

    [RelayCommand]
    private async Task EditAsync(SiteRow row)
    {
        await RefreshPhpAsync();
        Editor = NewEditor(row.Site);
    }

    /// <summary>An htdocs folder opens its saved entry, or a new one for that folder.</summary>
    [RelayCommand]
    private async Task EditHtdocsAsync(HtdocsRow row)
    {
        await RefreshPhpAsync();
        var saved = (await _api.GetSitesAsync()).FirstOrDefault(s => s.Kind == SiteKind.Htdocs && string.Equals(s.Name, row.Folder, StringComparison.OrdinalIgnoreCase));
        if (saved is not null)
        {
            Editor = NewEditor(saved);
            return;
        }

        var editor = NewEditor(null);
        editor.Kind = SiteKind.Htdocs;
        editor.FolderName = row.Folder;
        await editor.SetProjectFolderAsync(row.Entry.Root);
        Editor = editor;
    }

    [RelayCommand]
    private async Task ToggleAsync(SiteRow row)
    {
        var result = await CallAsync(() => _api.SetSiteEnabledAsync(row.Site.Id, !row.Site.Enabled));
        if (result is null) return;
        if (!result.Ok)
        {
            Say(Describe(result), ok: false);
            return;
        }
        await FollowUpAsync(result, $"{row.Name} is {(row.Site.Enabled ? "off" : "on")}.");
        await LoadAsync();
    }

    /// <summary>The first press asks; the second removes the site with its certificate and password file.</summary>
    [RelayCommand]
    private async Task RemoveAsync(SiteRow row)
    {
        if (!row.ConfirmingRemove)
        {
            row.ConfirmingRemove = true;
            return;
        }

        var result = await CallAsync(() => _api.RemoveSiteAsync(row.Site.Id));
        if (result is null) return;
        if (!result.Ok)
        {
            Say(Describe(result), ok: false);
            return;
        }
        await FollowUpAsync(result, $"{row.Name} is removed. Its logs stay in the logs folder.");
        await LoadAsync();
    }

    [RelayCommand]
    private static void CancelRemove(SiteRow row) => row.ConfirmingRemove = false;

    [RelayCommand]
    private static void Open(SiteRow row) => EditorLauncher.OpenUrl(row.Url);

    [RelayCommand]
    private static void OpenFolder(SiteRow row)
    {
        if (row.Site.Type != SiteType.Proxy) EditorLauncher.OpenFolder(row.Site.Root);
    }

    [RelayCommand]
    private void OpenHtdocs() => EditorLauncher.OpenFolder(HtdocsPath);

    [RelayCommand]
    private void OpenHtdocsEntry(HtdocsRow row) => EditorLauncher.OpenUrl($"http://{row.Local}");

    [RelayCommand]
    private void Trust()
    {
        OfferTrust = false;
        try
        {
            _ca.Trust();
            Say("Windows now trusts Anvil's certificate authority. Restart the browser to see the padlock.", ok: true);
        }
        catch (Exception e) when (e is CryptographicException or InvalidOperationException)
        {
            Say($"The certificate authority was not trusted: {e.Message}", ok: false);
        }
    }

    [RelayCommand]
    private void DismissTrust() => OfferTrust = false;

    private AddSiteViewModel NewEditor(SiteDefinition? editing) =>
        new(_api, _picker, new SiteFormContext(_php, _addresses(), _settings, HtdocsPath, _lanIp()), OnEditorDoneAsync,
            () => Editor = null, editing);

    private async Task OnEditorDoneAsync(SiteDefinition? site, ApplyResult result)
    {
        Editor = null;
        await FollowUpAsync(result, site is null ? "Copied into htdocs." : $"{site.Name} is saved.");
        if (site is { Cert: CertSource.LocalDev } && !IsCaTrusted()) OfferTrust = true;
        await LoadAsync();
    }

    private async Task FollowUpAsync(ApplyResult result, string success)
    {
        var problem = await _followUp.RunAsync(result, _settings.HttpPort, _settings.HttpsPort);
        Say(problem ?? success, ok: problem is null);
    }

    private bool IsCaTrusted()
    {
        try
        {
            return _ca.IsTrusted();
        }
        catch (CryptographicException e)
        {
            Log.Warning(e, "Reading the certificate store failed");
            return false;
        }
    }

    private async Task RefreshPhpAsync()
    {
        try
        {
            _php = await _api.GetPhpAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Reading PHP versions failed");
        }
    }

    private void Fill(IReadOnlyList<SiteDefinition> sites, IReadOnlyList<HtdocsEntry> htdocs)
    {
        var defaultPhp = _settings.DefaultPhp is { } v ? $"{v} (default)" : "default";
        Sites.Clear();
        foreach (var site in sites.Where(s => s.Kind != SiteKind.Htdocs).OrderBy(s => s.Kind == SiteKind.Ip ? 0 : 1).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            Sites.Add(new SiteRow
            {
                Site = site, Php = site.Type == SiteType.Php ? site.PhpVersion ?? defaultPhp : "none", Url = UrlOf(site),
            });
        Htdocs.Clear();
        var lan = _lanIp();
        foreach (var entry in htdocs)
        {
            var network = entry.Access != AccessLevel.Network ? AccessLabel(entry.Access)
                : _settings.Network.HtdocsOnNetwork && lan is not null ? $"{Host(lan)}/{entry.Folder}"
                : "Off";
            Htdocs.Add(new HtdocsRow(entry, $"{Host("localhost")}/{entry.Folder}", network, entry.PhpVersion ?? defaultPhp));
        }
        Filter();
        OnPropertyChanged(nameof(SitesTitle));
        OnPropertyChanged(nameof(HasSites));
        OnPropertyChanged(nameof(HtdocsTitle));
        OnPropertyChanged(nameof(HtdocsPath));
        OnPropertyChanged(nameof(LocalPattern));
        OnPropertyChanged(nameof(NetworkPattern));
    }

    private void Filter()
    {
        var text = SearchText.Trim();
        VisibleSites.Clear();
        foreach (var row in Sites.Where(r => text.Length == 0
                     || r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                     || r.Site.Aliases.Any(a => a.Contains(text, StringComparison.OrdinalIgnoreCase))
                     || r.Site.Root.Contains(text, StringComparison.OrdinalIgnoreCase)))
            VisibleSites.Add(row);
    }

    private string UrlOf(SiteDefinition site)
    {
        var https = site.Cert is CertSource.LetsEncrypt or CertSource.Imported or CertSource.LocalDev or CertSource.LoadBalancer;
        var host = site.Kind == SiteKind.Ip
            ? site.Name == "*" ? _lanIp() ?? "localhost" : site.Name.Contains(':') ? $"[{site.Name}]" : site.Name
            : site.Name;
        // Behind a load balancer the public address is https on 443 whatever this server listens on.
        var port = site.Cert == CertSource.LoadBalancer ? 443 : https ? _settings.HttpsPort : _settings.HttpPort;
        var hostPort = port == (https ? 443 : 80) ? host : $"{host}:{port}";
        return $"{(https ? "https" : "http")}://{hostPort}{(site.Kind == SiteKind.Ip ? "/" : "")}";
    }

    private string Host(string host) => _settings.HttpPort == 80 ? host : $"{host}:{_settings.HttpPort}";

    private void Say(string text, bool ok)
    {
        Message = text;
        MessageOk = ok;
    }

    private async Task<ApplyResult?> CallAsync(Func<Task<ApplyResult>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Agent call failed");
            Say($"The Anvil Agent did not answer: {e.Message}", ok: false);
            return null;
        }
    }

    private static string Describe(ApplyResult r) =>
        string.Join("\n", new[] { r.Error, r.ConfigTestOutput?.Trim() }.Where(t => !string.IsNullOrWhiteSpace(t)));
}
