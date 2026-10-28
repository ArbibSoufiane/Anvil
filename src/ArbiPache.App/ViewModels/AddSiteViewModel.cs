using System.Collections.ObjectModel;
using System.ComponentModel;
using ArbiPache.App.Services;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Certificates;
using ArbiPache.Core.Php;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ArbiPache.App.ViewModels;

/// <summary>What the site form needs from the rest of the app.</summary>
/// <param name="Addresses">This PC's IPv4 addresses, offered to IP sites.</param>
/// <param name="LanIp">The address other devices use to open htdocs, or null.</param>
public sealed record SiteFormContext(IReadOnlyList<PhpVersionInfo> Php, IReadOnlyList<string> Addresses, AppSettings Settings, string HtdocsDir,
    string? LanIp)
{
    /// <summary>Each saved site's certificate, by site id.</summary>
    public IReadOnlyDictionary<string, SiteCertificate> Certificates { get; init; } = new Dictionary<string, SiteCertificate>();
}

/// <summary>One entry of the PHP list: a version (null follows the default), no PHP, or forwarding to another server.</summary>
public sealed record PhpChoice(string Label, SiteType Type, string? Version);

/// <summary>An address an IP site can take; "*" is every address of this machine.</summary>
public sealed record AddressChoice(string Value, string Label);

/// <summary>One button of the "Who can open it" control.</summary>
public sealed partial class AccessChoice : ObservableObject
{
    public AccessChoice(AccessLevel level, string label, Action<AccessLevel> pick)
    {
        Level = level;
        Label = label;
        PickCommand = new RelayCommand(() => pick(level));
    }

    public AccessLevel Level { get; }
    public string Label { get; }
    public IRelayCommand PickCommand { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>An extra folder on an IP site, such as /demo.</summary>
public sealed partial class MountRow : ObservableObject
{
    public MountRow(IReadOnlyList<PhpChoice> php, Action<MountRow> remove, Func<MountRow, Task> browse)
    {
        PhpChoices = php;
        Php = php[0];
        RemoveCommand = new RelayCommand(() => remove(this));
        BrowseCommand = new AsyncRelayCommand(() => browse(this));
    }

    public IReadOnlyList<PhpChoice> PhpChoices { get; }
    public IRelayCommand RemoveCommand { get; }
    public IAsyncRelayCommand BrowseCommand { get; }

    [ObservableProperty]
    public partial string UrlPath { get; set; } = "/";

    [ObservableProperty]
    public partial string Root { get; set; } = "";

    [ObservableProperty]
    public partial PhpChoice? Php { get; set; }

    public SiteMount ToMount()
    {
        var choice = Php ?? PhpChoices[0];
        return new SiteMount(UrlPath.Trim(), Root.Trim(), choice.Type, choice.Type == SiteType.Php ? choice.Version : null);
    }
}

/// <summary>
/// The Add site / Edit site form: a domain site, an IP site (single site mode) or an htdocs folder. Saving validates
/// through the Agent, marks each error next to its field, and hands the result to <c>done</c> only when the Agent kept it.
/// </summary>
public sealed partial class AddSiteViewModel : ObservableObject
{
    private static readonly (AccessLevel Level, string Label)[] AccessLabels =
    [
        (AccessLevel.Everyone, "Everyone (internet)"),
        (AccessLevel.Network, "My network only"),
        (AccessLevel.ThisPc, "This PC only"),
        (AccessLevel.SpecificIps, "Specific IPs"),
    ];

    private readonly IAgentApi _api;
    private readonly IFolderPicker _picker;
    private readonly SiteFormContext _context;
    private readonly Func<SiteDefinition?, ApplyResult, Task> _done;
    private readonly Action _cancel;
    private SiteDefinition? _editing;
    private (SiteDefinition Site, ApplyResult Result)? _saved;
    private Detected? _detected;
    private string? _detectedFor;

    /// <param name="done">Called after the Agent kept the change; the site is null when a folder was only copied into htdocs.</param>
    public AddSiteViewModel(IAgentApi api, IFolderPicker picker, SiteFormContext context, Func<SiteDefinition?, ApplyResult, Task> done,
        Action cancel, SiteDefinition? editing = null)
    {
        _api = api;
        _picker = picker;
        _context = context;
        _done = done;
        _cancel = cancel;
        _editing = editing;

        var defaultPhp = context.Php.FirstOrDefault(p => p.IsDefault)?.Version;
        List<PhpChoice> php = [new(defaultPhp is null ? "Default PHP" : $"{defaultPhp} (default)", SiteType.Php, null)];
        php.AddRange(context.Php.Where(p => !p.IsDefault).Select(p => new PhpChoice(p.Version, SiteType.Php, p.Version)));
        if (editing is { PhpVersion: { } version } && php.All(c => c.Version != version))
            php.Add(new PhpChoice(context.Php.Any(p => p.Version == version) ? version : $"{version} (not installed)", SiteType.Php, version));
        php.Add(new PhpChoice("No PHP (static)", SiteType.Static, null));
        php.Add(new PhpChoice("Forward to another server (proxy)", SiteType.Proxy, null));
        PhpChoices = php;

        var port = context.Settings.HttpPort;
        List<AddressChoice> addresses = [.. context.Addresses.Select(a => new AddressChoice(a, $"{a} · port {port}"))];
        if (editing is { Kind: SiteKind.Ip } ip && ip.Name != "*" && addresses.All(a => a.Value != ip.Name))
            addresses.Add(new AddressChoice(ip.Name, $"{ip.Name} · port {port}"));
        addresses.Add(new AddressChoice("*", $"All addresses of this machine · port {port}"));
        Addresses = addresses;

        Mounts.CollectionChanged += (_, e) =>
        {
            foreach (MountRow row in e.NewItems ?? Array.Empty<MountRow>()) row.PropertyChanged += OnMountChanged;
            OnPropertyChanged(nameof(PreviewUrls));
        };

        if (editing is null)
        {
            SelectedPhp = PhpChoices[0];
            SelectedAddress = Addresses[0];
            Cert = DefaultCert(SiteKind.Domain);
        }
        else
        {
            Load(editing);
        }
        RebuildAccess();
    }

    public IReadOnlyList<PhpChoice> PhpChoices { get; }
    public IReadOnlyList<AddressChoice> Addresses { get; }
    public ObservableCollection<AccessChoice> AccessOptions { get; } = [];
    public ObservableCollection<MountRow> Mounts { get; } = [];

    public bool IsEditing => _editing is not null;
    public string Title => _editing is { } e ? $"Edit {e.Name}" : "Add a site";
    public string SaveLabel => IsEditing ? "Save" : "Create site";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDomain), nameof(IsIp), nameof(IsHtdocs), nameof(ShowCert), nameof(LetsEncryptEnabled), nameof(LetsEncryptHint),
        nameof(PreviewUrls), nameof(Footnote), nameof(ShowCopyChoice))]
    public partial SiteKind Kind { get; set; }

    public bool IsDomain => Kind == SiteKind.Domain;
    public bool IsIp => Kind == SiteKind.Ip;
    public bool IsHtdocs => Kind == SiteKind.Htdocs;
    public bool ShowCert => Kind != SiteKind.Htdocs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewUrls), nameof(Footnote))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Aliases { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewUrls), nameof(Footnote))]
    public partial AddressChoice? SelectedAddress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewUrls))]
    public partial string FolderName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCopyChoice))]
    public partial string ProjectFolder { get; set; } = "";

    [ObservableProperty]
    public partial string? DetectedText { get; set; }

    /// <summary>htdocs kind: copy the project into htdocs, or keep it where it is and answer through an alias.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepWhereItIs))]
    public partial bool CopyIntoHtdocs { get; set; }

    public bool KeepWhereItIs
    {
        get => !CopyIntoHtdocs;
        set => CopyIntoHtdocs = !value;
    }

    /// <summary>A folder already in htdocs needs neither a copy nor an alias.</summary>
    public bool ShowCopyChoice => IsHtdocs && !IsEditing && !IsInsideHtdocs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProxyTarget))]
    public partial PhpChoice? SelectedPhp { get; set; }

    public bool ShowProxyTarget => SelectedPhp?.Type == SiteType.Proxy;

    [ObservableProperty]
    public partial string ProxyTarget { get; set; } = ProjectDetector.ViteTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLetsEncrypt), nameof(IsImported), nameof(IsLoadBalancer), nameof(IsLocalDev), nameof(IsHttpOnly),
        nameof(PreviewUrls), nameof(Footnote), nameof(CurrentCertificateText))]
    public partial CertSource Cert { get; set; }

    public bool LetsEncryptEnabled => Kind == SiteKind.Domain;
    public string LetsEncryptHint => Kind == SiteKind.Ip ? "Not possible for a private IP address." : "Free, renews itself. Domain must point at this server.";

    public bool IsLetsEncrypt { get => Cert == CertSource.LetsEncrypt; set { if (value) Cert = CertSource.LetsEncrypt; } }
    public bool IsImported { get => Cert == CertSource.Imported; set { if (value) Cert = CertSource.Imported; } }
    public bool IsLoadBalancer { get => Cert == CertSource.LoadBalancer; set { if (value) Cert = CertSource.LoadBalancer; } }
    public bool IsLocalDev { get => Cert == CertSource.LocalDev; set { if (value) Cert = CertSource.LocalDev; } }
    public bool IsHttpOnly { get => Cert == CertSource.None; set { if (value) Cert = CertSource.None; } }

    [ObservableProperty]
    public partial string TrustedProxies { get; set; } = "";

    /// <summary>The client's certificate: a .pfx/.p12 file, or a PEM/DER certificate that needs <see cref="KeyFile"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPfx), nameof(ShowKeyFiles))]
    public partial string CertFile { get; set; } = "";

    [ObservableProperty]
    public partial string CertPassword { get; set; } = "";

    [ObservableProperty]
    public partial string KeyFile { get; set; } = "";

    [ObservableProperty]
    public partial string ChainFile { get; set; } = "";

    public bool IsPfx => Path.GetExtension(CertFile.Trim()).ToLowerInvariant() is ".pfx" or ".p12";
    public bool ShowKeyFiles => CertFile.Trim().Length > 0 && !IsPfx;

    public string? CurrentCertificateText =>
        _editing is { } e && Current(e.Id) is { } c ? $"Current certificate: {string.Join(", ", c.Names)}, expires {c.NotAfter:yyyy-MM-dd}." : null;

    /// <summary>Where the domain points, checked before Let's Encrypt is asked.</summary>
    [ObservableProperty]
    public partial string? DnsText { get; set; }

    [ObservableProperty]
    public partial bool Busy { get; set; }

    /// <summary>Once the site is saved, leaving the form keeps it: the button says Close.</summary>
    public string CancelLabel => _saved is null ? "Cancel" : "Close";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllowedIps), nameof(PreviewUrls))]
    public partial AccessLevel Access { get; set; }

    public bool ShowAllowedIps => Access == AccessLevel.SpecificIps;

    [ObservableProperty]
    public partial string AllowedIps { get; set; } = "";

    [ObservableProperty]
    public partial bool PasswordProtected { get; set; }

    [ObservableProperty]
    public partial string User { get; set; } = "";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    public string? PasswordHint => _editing is { PasswordProtected: true } ? "Leave both empty to keep the current user name and password." : null;

    [ObservableProperty]
    public partial string? NameError { get; set; }

    [ObservableProperty]
    public partial string? AliasesError { get; set; }

    [ObservableProperty]
    public partial string? RootError { get; set; }

    [ObservableProperty]
    public partial string? PhpError { get; set; }

    [ObservableProperty]
    public partial string? MountsError { get; set; }

    [ObservableProperty]
    public partial string? CertError { get; set; }

    [ObservableProperty]
    public partial string? TrustedProxiesError { get; set; }

    [ObservableProperty]
    public partial string? AccessError { get; set; }

    [ObservableProperty]
    public partial string? GeneralError { get; set; }

    public IReadOnlyList<string> PreviewUrls
    {
        get
        {
            var https = Cert is CertSource.LetsEncrypt or CertSource.Imported or CertSource.LocalDev;
            switch (Kind)
            {
                case SiteKind.Domain:
                    var name = Name.Trim().ToLowerInvariant();
                    if (name.Length == 0) return [];
                    if (Cert == CertSource.LoadBalancer)
                        return [$"https://{name}  (TLS at load balancer)", $"http://{WithPort(name, false)}  (load balancer → this server)"];
                    return [(https ? "https://" : "http://") + WithPort(name, https)];
                case SiteKind.Ip:
                    if (AddressShown is not { } address) return [];
                    var host = (https ? "https://" : "http://") + WithPort(address.Contains(':') ? $"[{address}]" : address, https);
                    return [host + "/", .. Mounts.Select(m => m.UrlPath.Trim().Trim('/')).Where(p => p.Length > 0).Select(p => $"{host}/{p}")];
                default:
                    var folder = FolderName.Trim();
                    if (folder.Length == 0) return [];
                    List<string> urls = [$"http://{WithPort("localhost", false)}/{folder}"];
                    if (Access == AccessLevel.Network && _context.Settings.Network.HtdocsOnNetwork && _context.LanIp is { } lan)
                        urls.Add($"http://{WithPort(lan, false)}/{folder}");
                    return urls;
            }
        }
    }

    public string Footnote => Kind switch
    {
        SiteKind.Ip when SelectedAddress?.Value == "*" => "This replaces the htdocs listing on every address of this machine. Domain sites keep working.",
        SiteKind.Ip => $"This replaces the htdocs listing on {SelectedAddress?.Value}. Domain sites keep working.",
        SiteKind.Htdocs => "htdocs folders are never served to the internet.",
        _ => Cert switch
        {
            CertSource.LetsEncrypt => "Domain must point at this server. Port 80 must be reachable for Let's Encrypt.",
            CertSource.LocalDev => "Trusted on this PC only. ArbiPache adds .test names to the hosts file for you.",
            CertSource.LoadBalancer => "The domain points at the load balancer; the load balancer forwards to this server.",
            _ => "Domain must point at this server.",
        },
    };

    private string? AddressShown => SelectedAddress?.Value == "*" ? _context.LanIp ?? "localhost" : SelectedAddress?.Value;

    private bool IsInsideHtdocs
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProjectFolder)) return false;
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(ProjectFolder.Trim()).TrimEnd('\\'));
                return string.Equals(parent, Path.GetFullPath(_context.HtdocsDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }

    private string ServeRoot => _detected is { } d && _detectedFor == ProjectFolder ? d.ServeRoot : ProjectFolder.Trim();

    partial void OnKindChanged(SiteKind value)
    {
        if (!IsEditing) Cert = DefaultCert(value);
        else if (Cert == CertSource.LetsEncrypt && !LetsEncryptEnabled) Cert = DefaultCert(value);
        if (value == SiteKind.Htdocs) Access = AccessLevel.Network;
        RebuildAccess();
    }

    // An option the kind does not offer falls back to the kind's default.
    partial void OnCertChanged(CertSource value)
    {
        if (value == CertSource.LetsEncrypt && !LetsEncryptEnabled) Cert = DefaultCert(Kind);
    }

    partial void OnAccessChanged(AccessLevel value)
    {
        foreach (var option in AccessOptions) option.IsSelected = option.Level == value;
    }

    [RelayCommand]
    private void PickDomain() => Kind = SiteKind.Domain;

    [RelayCommand]
    private void PickIp() => Kind = SiteKind.Ip;

    [RelayCommand]
    private void PickHtdocs() => Kind = SiteKind.Htdocs;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _picker.PickFolderAsync("Choose the project folder") is { } folder) await SetProjectFolderAsync(folder);
    }

    /// <summary>Takes a project folder and fills what detection finds: the folder to serve, the type, a proxy target.</summary>
    public async Task SetProjectFolderAsync(string folder)
    {
        ProjectFolder = folder;
        if (IsHtdocs && string.IsNullOrWhiteSpace(FolderName)) FolderName = Path.GetFileName(folder.TrimEnd('\\'));
        Detected detected;
        try
        {
            detected = await _api.DetectAsync(folder);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Project detection failed");
            DetectedText = null;
            return;
        }

        _detected = detected;
        _detectedFor = folder;
        DetectedText = Describe(detected, folder);
        SelectedPhp = detected.Type switch
        {
            SiteType.Static => PhpChoices.First(c => c.Type == SiteType.Static),
            SiteType.Proxy => PhpChoices.First(c => c.Type == SiteType.Proxy),
            _ => SelectedPhp is { Type: SiteType.Php } current ? current : PhpChoices[0],
        };
        if (detected.ProxyTarget is { } target) ProxyTarget = target;
    }

    [RelayCommand]
    private void AddMount()
    {
        var row = new MountRow(PhpChoices, r => Mounts.Remove(r), BrowseMountAsync);
        Mounts.Add(row);
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (_saved is { } saved) await _done(saved.Site, saved.Result);
        else _cancel();
    }

    [RelayCommand]
    private async Task ChooseCertFileAsync()
    {
        if (await _picker.PickFileAsync("Choose the certificate the client sent", "Certificates", ["*.pfx", "*.p12", "*.crt", "*.cer", "*.pem"]) is { } file)
            CertFile = file;
    }

    [RelayCommand]
    private async Task ChooseKeyFileAsync()
    {
        if (await _picker.PickFileAsync("Choose the private key", "Private keys", ["*.key", "*.pem"]) is { } file) KeyFile = file;
    }

    [RelayCommand]
    private async Task ChooseChainFileAsync()
    {
        if (await _picker.PickFileAsync("Choose the intermediate certificates", "Certificates", ["*.crt", "*.pem", "*.ca-bundle"]) is { } file)
            ChainFile = file;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ClearErrors();
        DnsText = null;
        var site = Build();
        if (MissingCertificateFiles(site) is { } missing)
        {
            CertError = missing;
            return;
        }

        try
        {
            var errors = await _api.ValidateSiteAsync(site);
            if (errors.Count > 0)
            {
                Show(errors);
                return;
            }

            ApplyResult? copied = null;
            if (IsHtdocs && !IsEditing && !IsInsideHtdocs && CopyIntoHtdocs)
            {
                copied = await _api.CopyIntoHtdocsAsync(ProjectFolder.Trim(), FolderName.Trim());
                if (!copied.Ok)
                {
                    GeneralError = Describe(copied);
                    return;
                }
            }

            // A folder in htdocs with the htdocs defaults needs no saved entry: the htdocs root serves it.
            if (IsHtdocs && !IsEditing && (copied is not null || IsInsideHtdocs) && !NeedsHtdocsEntry(site))
            {
                await _done(null, copied ?? ApplyResult.Success());
                return;
            }

            var secrets = PasswordProtected && User.Trim().Length > 0 && Password.Length > 0 ? new SiteSecrets(User.Trim(), Password) : null;
            var result = await _api.SaveSiteAsync(site, secrets);
            if (!result.Ok)
            {
                GeneralError = Describe(result);
                return;
            }

            KeepEditing(site, result);
            if (!await GetCertificateAsync(site)) return;
            await _done(site, result);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Saving a site failed");
            GeneralError = $"The ArbiPache Agent did not answer: {e.Message}";
        }
    }

    /// <summary>A new client certificate needs its file, and a PEM certificate its key; editing may keep the current one.</summary>
    private string? MissingCertificateFiles(SiteDefinition site)
    {
        if (site.Cert != CertSource.Imported) return null;
        if (CertFile.Trim().Length == 0)
            return Current(site.Id) is null ? "Choose the certificate file the client sent." : null;
        return !IsPfx && KeyFile.Trim().Length == 0 ? "Choose the private key file that goes with the certificate." : null;
    }

    /// <summary>
    /// After the site is saved: imports the chosen certificate, or asks Let's Encrypt when every name resolves and the
    /// current certificate does not cover them. False keeps the form open with the reason; the site stays saved.
    /// </summary>
    private async Task<bool> GetCertificateAsync(SiteDefinition site)
    {
        if (site.Cert == CertSource.Imported && CertFile.Trim().Length > 0)
        {
            try
            {
                await _api.ImportCertificateAsync(site.Id, ImportRequest());
                CertFile = KeyFile = ChainFile = CertPassword = "";
                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                CertError = CertificateMessages.For(e);
                return false;
            }
        }

        if (site.Cert != CertSource.LetsEncrypt || Covered(site)) return true;
        var lines = new List<string>();
        var unresolved = new List<string>();
        foreach (var name in site.Aliases.Prepend(site.Name))
        {
            var addresses = await _api.ResolveAsync(name);
            if (addresses.Count == 0) unresolved.Add(name);
            else lines.Add($"{name} points to {string.Join(", ", addresses)}.");
        }

        if (unresolved.Count > 0)
            lines.Add($"{string.Join(", ", unresolved)} {(unresolved.Count == 1 ? "does" : "do")} not point to any address yet. " +
                "Point the domain at this server; ArbiPache asks Let's Encrypt every night until the certificate is issued.");
        DnsText = string.Join("\n", lines);
        if (unresolved.Count > 0) return false;

        Busy = true;
        try
        {
            await _api.IssueLetsEncryptAsync(site.Id, _context.Settings.AcmeStaging);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            CertError = $"Let's Encrypt did not issue the certificate. {e.Message} ArbiPache tries again every night.";
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Reads the chosen files here: the Agent may run as a service account that cannot open the person's folders.</summary>
    private CertImportRequest ImportRequest() => IsPfx
        ? new CertImportRequest(CertImportFile.Read(CertFile.Trim()), CertPassword, null, null, null)
        : new CertImportRequest(null, null, CertImportFile.Read(CertFile.Trim()), CertImportFile.Read(KeyFile.Trim()),
            ChainFile.Trim() is { Length: > 0 } chain ? CertImportFile.Read(chain) : null);

    private SiteCertificate? Current(string siteId) =>
        _context.Certificates.TryGetValue(siteId, out var c) && c.Source == Cert ? c : null;

    private bool Covered(SiteDefinition site) =>
        Current(site.Id) is { } c && site.Aliases.Prepend(site.Name).All(n => NameMatch.Covers(c.Names, n));

    /// <summary>The site is saved: from here the form edits it, and leaving reports it to the sites page.</summary>
    private void KeepEditing(SiteDefinition site, ApplyResult result)
    {
        _editing = site;
        _saved = (site, result);
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(CancelLabel));
        OnPropertyChanged(nameof(ShowCopyChoice));
    }

    private SiteDefinition Build()
    {
        var choice = SelectedPhp ?? PhpChoices[0];
        var name = Kind switch
        {
            SiteKind.Domain => Name.Trim().ToLowerInvariant(),
            SiteKind.Ip => SelectedAddress?.Value ?? "",
            _ => FolderName.Trim(),
        };
        var root = choice.Type == SiteType.Proxy ? ProxyTarget.Trim()
            : IsHtdocs && !IsEditing && !IsInsideHtdocs && CopyIntoHtdocs ? Path.Combine(_context.HtdocsDir, FolderName.Trim())
            : ServeRoot;
        var basis = _editing ?? new SiteDefinition { Id = "", Kind = Kind, Type = choice.Type, Name = name, Root = root };
        return basis with
        {
            Id = _editing?.Id ?? SiteSet.IdFor(Kind, name),
            Kind = Kind,
            Type = choice.Type,
            Name = name,
            Root = root,
            PhpVersion = choice.Type == SiteType.Php ? choice.Version : null,
            Aliases = IsDomain ? Split(Aliases) : [],
            Mounts = IsIp ? Mounts.Select(m => m.ToMount()).ToList() : [],
            Cert = IsHtdocs ? CertSource.None : Cert,
            TrustedProxies = !IsHtdocs && Cert == CertSource.LoadBalancer ? Split(TrustedProxies) : [],
            Access = Access,
            AllowedIps = Access == AccessLevel.SpecificIps ? Split(AllowedIps) : [],
            PasswordProtected = PasswordProtected,
        };
    }

    private void Load(SiteDefinition e)
    {
        Kind = e.Kind;
        Name = e.Kind == SiteKind.Domain ? e.Name : "";
        FolderName = e.Kind == SiteKind.Htdocs ? e.Name : "";
        SelectedAddress = Addresses.FirstOrDefault(a => a.Value == e.Name) ?? Addresses[0];
        ProjectFolder = e.Type == SiteType.Proxy ? "" : e.Root;
        if (e.Type == SiteType.Proxy) ProxyTarget = e.Root;
        SelectedPhp = e.Type switch
        {
            SiteType.Static => PhpChoices.First(c => c.Type == SiteType.Static),
            SiteType.Proxy => PhpChoices.First(c => c.Type == SiteType.Proxy),
            _ => PhpChoices.First(c => c.Type == SiteType.Php && c.Version == e.PhpVersion),
        };
        Aliases = string.Join(" ", e.Aliases);
        foreach (var mount in e.Mounts)
        {
            AddMount();
            var row = Mounts[^1];
            row.UrlPath = mount.UrlPath;
            row.Root = mount.Root;
            row.Php = PhpChoices.FirstOrDefault(c => c.Type == mount.Type && c.Version == mount.PhpVersion) ?? PhpChoices[0];
        }
        Cert = e.Cert;
        TrustedProxies = string.Join(" ", e.TrustedProxies);
        Access = e.Access;
        AllowedIps = string.Join(" ", e.AllowedIps);
        PasswordProtected = e.PasswordProtected;
    }

    private void RebuildAccess()
    {
        AccessOptions.Clear();
        foreach (var (level, label) in AccessLabels.Where(a => !(IsHtdocs && a.Level == AccessLevel.Everyone)))
            AccessOptions.Add(new AccessChoice(level, label, l => Access = l) { IsSelected = level == Access });
    }

    private async Task BrowseMountAsync(MountRow row)
    {
        if (await _picker.PickFolderAsync("Choose the folder for " + row.UrlPath) is not { } folder) return;
        row.Root = folder;
        try
        {
            var detected = await _api.DetectAsync(folder);
            row.Root = detected.Type == SiteType.Proxy ? folder : detected.ServeRoot;
            if (detected.Type == SiteType.Static) row.Php = PhpChoices.First(c => c.Type == SiteType.Static);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Project detection failed");
        }
    }

    private void OnMountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MountRow.UrlPath)) OnPropertyChanged(nameof(PreviewUrls));
    }

    private CertSource DefaultCert(SiteKind kind) => (kind, _context.Settings.Mode) switch
    {
        (SiteKind.Domain, ServerMode.Production) => CertSource.LetsEncrypt,
        (SiteKind.Domain, _) => CertSource.LocalDev,
        (SiteKind.Ip, ServerMode.Production) => CertSource.LoadBalancer,
        _ => CertSource.None,
    };

    private string WithPort(string host, bool https)
    {
        var port = https ? _context.Settings.HttpsPort : _context.Settings.HttpPort;
        return port == (https ? 443 : 80) ? host : $"{host}:{port}";
    }

    private static bool NeedsHtdocsEntry(SiteDefinition site) =>
        site.Access != SiteSet.DefaultHtdocsAccess || site.PhpVersion is not null || site.PasswordProtected || site.Type != SiteType.Php;

    private void ClearErrors()
    {
        NameError = AliasesError = RootError = PhpError = MountsError = CertError = TrustedProxiesError = AccessError = GeneralError = null;
    }

    private void Show(IReadOnlyList<ValidationError> errors)
    {
        var other = new List<string>();
        foreach (var error in errors)
        {
            switch (error.Field)
            {
                case nameof(SiteDefinition.Name): NameError = error.Message; break;
                case nameof(SiteDefinition.Aliases): AliasesError = error.Message; break;
                case nameof(SiteDefinition.Root): RootError = error.Message; break;
                case nameof(SiteDefinition.PhpVersion): PhpError = error.Message; break;
                case nameof(SiteDefinition.Mounts): MountsError = error.Message; break;
                case nameof(SiteDefinition.Cert): CertError = error.Message; break;
                case nameof(SiteDefinition.TrustedProxies): TrustedProxiesError = error.Message; break;
                case nameof(SiteDefinition.Access) or nameof(SiteDefinition.AllowedIps): AccessError = error.Message; break;
                default: other.Add(error.Message); break;
            }
        }
        if (other.Count > 0) GeneralError = string.Join(" ", other);
    }

    private static string Describe(Detected d, string folder)
    {
        var what = d.Label switch
        {
            "PHP" => "PHP project.",
            "Static" => "Static site detected.",
            _ => $"{d.Label} detected.",
        };
        if (d.Type == SiteType.Proxy && d.ProxyTarget is { } target) return $"{what} Requests go to {target}.";
        var relative = Path.GetRelativePath(folder, d.ServeRoot);
        return relative == "." ? what : $"{what} Serving the {relative}\\ folder.";
    }

    private static string Describe(ApplyResult r) =>
        string.Join("\n", new[] { r.Error, r.ConfigTestOutput?.Trim() }.Where(t => !string.IsNullOrWhiteSpace(t)));

    private static IReadOnlyList<string> Split(string text) =>
        text.Split([' ', ',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
}
