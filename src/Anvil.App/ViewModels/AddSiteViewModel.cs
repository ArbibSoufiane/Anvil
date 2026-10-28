using System.Collections.ObjectModel;
using Anvil.App.Services;
using Anvil.Core.Agent;
using Anvil.Core.Certificates;
using Anvil.Core.Php;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Anvil.App.ViewModels;

/// <summary>What the site form needs from the rest of the app.</summary>
/// <param name="LanIp">The address other devices use to open htdocs, or null.</param>
public sealed record SiteFormContext(IReadOnlyList<PhpVersionInfo> Php, AppSettings Settings, string HtdocsDir, string? LanIp);

/// <summary>One entry of the PHP list: a version (null follows the default), no PHP, or forwarding to another server.</summary>
public sealed record PhpChoice(string Label, SiteType Type, string? Version);

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

/// <summary>
/// The Add site / Edit site form: a domain site or an htdocs folder. Saving validates
/// through the Agent, marks each error next to its field, and hands the result to <c>done</c> only when the Agent kept it.
/// </summary>
public sealed partial class AddSiteViewModel : ObservableObject
{
    private static readonly (AccessLevel Level, string Label)[] AccessLabels =
    [
        (AccessLevel.ThisPc, "This PC only"),
        (AccessLevel.Network, "My network"),
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

        if (editing is null)
        {
            SelectedPhp = PhpChoices[0];
            Cert = DefaultCert(SiteKind.Domain);
            Access = AccessLevel.Network;
        }
        else
        {
            Load(editing);
        }
        RebuildAccess();
    }

    public IReadOnlyList<PhpChoice> PhpChoices { get; }
    public ObservableCollection<AccessChoice> AccessOptions { get; } = [];

    public bool IsEditing => _editing is not null;
    public string Title => _editing is { } e ? $"Edit {e.Name}" : "Add a site";
    public string SaveLabel => IsEditing ? "Save" : "Create site";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDomain), nameof(IsHtdocs), nameof(ShowCert),
        nameof(PreviewUrls), nameof(Footnote), nameof(ShowCopyChoice))]
    public partial SiteKind Kind { get; set; }

    public bool IsDomain => Kind == SiteKind.Domain;
    public bool IsHtdocs => Kind == SiteKind.Htdocs;
    public bool ShowCert => Kind != SiteKind.Htdocs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewUrls), nameof(Footnote))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Aliases { get; set; } = "";

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
    [NotifyPropertyChangedFor(nameof(IsLocalDev), nameof(IsHttpOnly), nameof(PreviewUrls), nameof(Footnote))]
    public partial CertSource Cert { get; set; }

    public bool IsLocalDev { get => Cert == CertSource.LocalDev; set { if (value) Cert = CertSource.LocalDev; } }
    public bool IsHttpOnly { get => Cert == CertSource.None; set { if (value) Cert = CertSource.None; } }

    [ObservableProperty]
    public partial bool Busy { get; set; }

    /// <summary>Once the site is saved, leaving the form keeps it: the button says Close.</summary>
    public string CancelLabel => _saved is null ? "Cancel" : "Close";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewUrls))]
    public partial AccessLevel Access { get; set; }

    [ObservableProperty]
    public partial string? NameError { get; set; }

    [ObservableProperty]
    public partial string? AliasesError { get; set; }

    [ObservableProperty]
    public partial string? RootError { get; set; }

    [ObservableProperty]
    public partial string? PhpError { get; set; }

    [ObservableProperty]
    public partial string? CertError { get; set; }

    [ObservableProperty]
    public partial string? AccessError { get; set; }

    [ObservableProperty]
    public partial string? GeneralError { get; set; }

    public IReadOnlyList<string> PreviewUrls
    {
        get
        {
            var https = Cert == CertSource.LocalDev;
            switch (Kind)
            {
                case SiteKind.Domain:
                    var name = Name.Trim().ToLowerInvariant();
                    return name.Length == 0 ? [] : [(https ? "https://" : "http://") + WithPort(name, https)];
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
        SiteKind.Htdocs => "htdocs folders are never served to the internet.",
        _ when Cert == CertSource.LocalDev => "Trusted on this PC only. Anvil adds .test names to the hosts file for you.",
        _ => "Anvil adds .test names to the hosts file for you.",
    };

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
    }

    partial void OnAccessChanged(AccessLevel value)
    {
        foreach (var option in AccessOptions) option.IsSelected = option.Level == value;
    }

    [RelayCommand]
    private void PickDomain() => Kind = SiteKind.Domain;

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
    private async Task CancelAsync()
    {
        if (_saved is { } saved) await _done(saved.Site, saved.Result);
        else _cancel();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ClearErrors();
        var site = Build();

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

            var result = await _api.SaveSiteAsync(site);
            if (!result.Ok)
            {
                GeneralError = Describe(result);
                return;
            }

            KeepEditing(site, result);
            await _done(site, result);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Saving a site failed");
            GeneralError = $"The Anvil Agent did not answer: {e.Message}";
        }
    }


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
        var name = IsDomain ? Name.Trim().ToLowerInvariant() : FolderName.Trim();
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
            Cert = IsHtdocs ? CertSource.None : Cert,
            Access = Access,
        };
    }

    private void Load(SiteDefinition e)
    {
        Kind = e.Kind;
        Name = e.Kind == SiteKind.Domain ? e.Name : "";
        FolderName = e.Kind == SiteKind.Htdocs ? e.Name : "";
        ProjectFolder = e.Type == SiteType.Proxy ? "" : e.Root;
        if (e.Type == SiteType.Proxy) ProxyTarget = e.Root;
        SelectedPhp = e.Type switch
        {
            SiteType.Static => PhpChoices.First(c => c.Type == SiteType.Static),
            SiteType.Proxy => PhpChoices.First(c => c.Type == SiteType.Proxy),
            _ => PhpChoices.First(c => c.Type == SiteType.Php && c.Version == e.PhpVersion),
        };
        Aliases = string.Join(" ", e.Aliases);
        Cert = e.Cert;
        Access = e.Access;
    }

    private void RebuildAccess()
    {
        AccessOptions.Clear();
        foreach (var (level, label) in AccessLabels)
            AccessOptions.Add(new AccessChoice(level, label, l => Access = l) { IsSelected = level == Access });
    }

    private static CertSource DefaultCert(SiteKind kind) => kind == SiteKind.Domain ? CertSource.LocalDev : CertSource.None;

    private string WithPort(string host, bool https)
    {
        var port = https ? _context.Settings.HttpsPort : _context.Settings.HttpPort;
        return port == (https ? 443 : 80) ? host : $"{host}:{port}";
    }

    private static bool NeedsHtdocsEntry(SiteDefinition site) =>
        site.Access != SiteSet.DefaultHtdocsAccess || site.PhpVersion is not null || site.Type != SiteType.Php;

    private void ClearErrors()
    {
        NameError = AliasesError = RootError = PhpError = CertError = AccessError = GeneralError = null;
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
                case nameof(SiteDefinition.Cert): CertError = error.Message; break;
                case nameof(SiteDefinition.Access): AccessError = error.Message; break;
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
