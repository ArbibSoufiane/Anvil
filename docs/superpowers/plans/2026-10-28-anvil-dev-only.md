# Anvil (development-only ArbiPache) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename ArbiPache 4 to Anvil 1.0.0, remove every production feature, add network sharing (per-site This PC / My network, `<label>.<network domain>` names answered by a built-in DNS responder, per-program firewall rules), fix eight review leftovers, and ship the licence, signing guide and manual test checklist.

**Architecture:** Same three executables and one library (window, Agent, Elevator, Core). Removal tasks delete code top-down and let the compiler find the rest. New network code lives in `Anvil.Core.Network` (pure planners and codecs, unit-tested) with thin Windows adapters (COM, sockets) wired into the Agent's `Orchestrator`. The window gets a Network sharing card and copy buttons.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), Avalonia 12.1.4, CommunityToolkit.Mvvm, StreamJsonRpc, Scriban, xunit.v3 on Microsoft.Testing.Platform.

**Spec:** `docs/superpowers/specs/2026-10-28-anvil-dev-only-design.md` (builds on `docs/superpowers/specs/2026-10-27-arbipache4-design.md`). Review leftovers M1–M11 are described in the rulings log, https://claude.ai/artifact/31yu39B1pwxcGbNHcgieNP.

## Global Constraints

- Product name **Anvil**; version **1.0.0**, defined only as `<Version>` in `Directory.Build.props`, read through `Anvil.Core.BuildInfo.Version`.
- Licence **GPL-3.0** (verbatim gnu.org text).
- Commits use conventional prefixes (`feat:`, `fix:`, `test:`, `chore:`, `docs:`). **No `Co-Authored-By` line and no AI-attribution line of any kind** in commit messages (the owner's global instruction overrides any default).
- Work on branch `feat/arbipache4`. There is no git remote; never push.
- `TreatWarningsAsErrors` stays on. Packages stay pinned centrally in `Directory.Packages.props`; delete a package reference as soon as nothing uses it.
- Nothing is reachable from the internet: every firewall rule Anvil makes has profiles `3` (Domain | Private) and remote addresses `LocalSubnet`.
- Access levels are exactly `ThisPc` (`Require local`) and `Network` (`Require ip ` + `ConfigRenderer.PrivateRanges`). New domain sites default to `Network`.
- Network domain: empty by default (feature off); placeholder text `<computer name>.internal`.
- DNS responder: UDP only, port 53 outside tests, one socket per LAN address (never `0.0.0.0`), answers only private-range askers, `A` → the address the query arrived on with TTL 60, other types under the domain → empty `NOERROR`, names outside the domain → `REFUSED`, never forwards.
- User-facing copy: plain sentences, Anvil's existing tone, no references to removed features.
- Unit tests: `dotnet test --project tests/Anvil.Core.Tests` and `dotnet test --project tests/Anvil.App.Tests`; one class: append `-- --filter-class "*ClassName"`. Integration: `dotnet test --project tests/Anvil.Integration.Tests`. (Before Task 1 the folders are named `ArbiPache.*`.)
- Tests of removed features are deleted with the feature; every other test stays green after every task.

## Review Focus

1. **DNS queries in mixed case** (`ShOp.ArBiB.pC`, the "0x20" trick many resolvers use): answered, with the question section echoed byte for byte. Test: Task 10 `Mixed_case_query_is_answered_and_echoed_exactly`.
2. **Queries carrying an EDNS OPT record** (ARCOUNT = 1, sent by Windows and macOS resolvers): answered normally, no OPT in the reply. Test: Task 10 `Query_with_edns_record_is_answered`.
3. **htdocs folder names with capitals, spaces, accents or underscores** (`MyApp`, `my app`, `café`, `old_site`): capitals are lower-cased, the rest are reported on the Sites page, and no broken vhost is ever rendered. Tests: Task 8 `Folder_names_are_lower_cased_or_reported`, Task 9 `Folder_without_network_name_renders_no_network_file`.
4. **This PC's LAN address changes while Anvil runs** (DHCP renewal, Wi-Fi to Ethernet): the responder moves to the new address, and the findings and copy commands show it. Tests: Task 12 `Address_change_moves_the_name_service`, Task 13 `Commands_follow_the_current_address`.
5. **Port 53 free on one address and taken on another, or a client that vanishes before its reply** (ICMP "port unreachable"): the other addresses keep answering and the receive loop survives. Tests: Task 11 `Busy_address_is_reported_and_others_answer`, `Client_that_vanished_does_not_stop_it`.

---

### Task 1: Rename ArbiPache to Anvil

**Files:**
- Rename: `ArbiPache.sln` → `Anvil.sln`; `src/ArbiPache.{Core,App,Agent,Elevator}` → `src/Anvil.*` (and each `.csproj`); `tests/ArbiPache.{Core.Tests,App.Tests,Integration.Tests,TestChild}` → `tests/Anvil.*`
- Modify (text): everything under `src/`, `tests/`, `build/`, `.github/`, `catalog/`, `Directory.Build.props`, `Directory.Packages.props`, `CLAUDE.md`
- Create: `tests/Anvil.Core.Tests/NamingTests.cs`
- Leave alone: `docs/` (history), `README.md`, `CHANGELOG.md`, `LICENSE`, `SECURITY.md`, `CONTRIBUTING.md`, `packaging/README.txt` (rewritten in Task 21)

**Interfaces:**
- Produces: namespaces `Anvil.Core.*`, `Anvil.App.*`, `Anvil.Agent`, `Anvil.Elevator`; executables `Anvil.exe`, `Anvil.Agent.exe`, `Anvil.Elevator.exe`; settings file `data\anvil.json`; hosts markers `# >>> Anvil >>>` / `# <<< Anvil <<<`; release feed default `ArbibSoufiane/Anvil`, zip `Anvil-<version>-win64.zip`; `<Product>Anvil</Product>`, `<Version>1.0.0</Version>`.

- [ ] **Step 1: Record the baseline.** Run the three suites under the old names. Expected: all pass (the rulings log recorded Core 626, App 183, Integration 31). Note the counts in the task report.
- [ ] **Step 2: Write the failing guard test** in `tests/ArbiPache.Core.Tests/NamingTests.cs` (it moves with the rename):

```csharp
public sealed class NamingTests
{
    // Lines that name the old program on purpose: the ArbiPache 3 import, and cleanup of files ArbiPache 4 test builds left.
    private static readonly Regex Allowed = new(@"ArbiPache 3|ARBIPACHE_HOME|\.arbipache\b|legacy-name", RegexOptions.IgnoreCase);

    [Fact]
    public void Old_name_appears_only_where_it_refers_to_the_old_program()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Repo.Root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".axaml") || f.EndsWith(".sbn") || f.EndsWith(".csproj") || f.EndsWith(".manifest"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => x.line.Contains("arbipache", StringComparison.OrdinalIgnoreCase) && !Allowed.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(Repo.Root, x.f)}:{x.i + 1}")
            .ToList();
        Assert.Empty(offenders);
    }
}
```

- [ ] **Step 3: Run it.** `dotnet test --project tests/ArbiPache.Core.Tests -- --filter-class "*NamingTests"`. Expected: FAIL with hundreds of offenders.
- [ ] **Step 4: Rename.** `git mv` the solution, project folders and `.csproj` files. Then replace text in the listed files in this order: protect the tokens `ArbiPache 3`, `ARBIPACHE_HOME`, `.arbipache` and `ArbibSoufiane/ArbiPache/tree` with placeholders; replace `ArbiPache` → `Anvil`, `arbipache` → `anvil`, `ARBIPACHE` → `ANVIL`; restore the placeholders. Update `Repo.cs` and `PublishLayoutTests.cs` to look for `Anvil.sln`. In `Directory.Build.props` set `<Version>1.0.0</Version>`. In `CLAUDE.md` change the versioning examples to `1.0.0` → `2.0.0` / `1.0.0` → `1.1.0` / `1.1.0` → `1.1.1`. Review the diff of `LegacyImporter.cs`, `HostsFile.cs` (the `ApacheManager` legacy markers must be unchanged) and `ReleaseFeed.cs`.
- [ ] **Step 5: Build and run everything.** `dotnet build Anvil.sln`, then the three suites under the new names. Expected: all pass, `NamingTests` included. Snapshot files (`*.verified.txt`) were renamed by the text pass; a remaining mismatch means a missed occurrence.
- [ ] **Step 6: Commit.** `git add -A && git commit -m "chore: rename ArbiPache to Anvil and start at 1.0.0"`

### Task 2: Remove the Windows service and folder security

**Files:**
- Delete: `src/Anvil.App/Services/ServiceSwitch.cs`, `src/Anvil.App/Services/WindowsAgentService.cs`, `src/Anvil.Elevator/Handlers/{ServiceHandler,AclHandler,FolderHandler}.cs`, `src/Anvil.Core/Elevation/{ServiceFolderCheck,AclPlanner,FolderPermissions}.cs`, `tests/Anvil.App.Tests/ServiceToggleTests.cs`, `tests/Anvil.Core.Tests/Elevation/{ServiceFolderCheckTests,AclPlannerTests,FolderPermissionsTests}.cs`
- Modify: `src/Anvil.Core/Elevation/ElevatedRequest.cs`, `RequestValidator.cs`, `src/Anvil.Elevator/Program.cs`, `src/Anvil.Core/Agent/{ApplyResult,AgentStatus}.cs`, `src/Anvil.Core/Settings/AppSettings.cs`, `src/Anvil.Agent/{Program,AgentHost,Orchestrator,PipeServer}.cs`, `src/Anvil.App/Services/ElevationFollowUp.cs`, `src/Anvil.App/ViewModels/SettingsViewModel.cs`, `src/Anvil.App/Views/SettingsView.axaml`, `Directory.Packages.props` and the csproj files
- Test: `tests/Anvil.Core.Tests/Elevation/RequestValidatorTests.cs`, `tests/Anvil.Core.Tests/Settings/SettingsStoreTests.cs`

**Interfaces:**
- Produces: `ElevatedRequest` derived types are only `HostsSetRequest` and `FirewallSetRequest`. `ApplyResult` has no `RequiredGrants`. `AgentStatus(ServerMode Mode, IReadOnlyList<ServiceStatus> Services, IReadOnlyList<PreflightFinding> Findings, string AgentVersion)` (Mode goes in Task 5). `AppSettings` has no `RunAsService` or `ControlUserSids`. The Agent runs only as the window's child.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Theory]
[InlineData("""{"verb":"service-install","agentExe":"x","controlUserSid":"S-1-5-21-1-2-3-1001"}""")]
[InlineData("""{"verb":"service-uninstall"}""")]
[InlineData("""{"verb":"acl-grant","grants":[]}""")]
[InlineData("""{"verb":"folder-secure","userSid":"S-1-5-21-1-2-3-1001"}""")]
public void Removed_verbs_are_unknown_requests(string json)
{
    var (request, error) = RequestValidator.Read(WriteRequest(json), _paths);   // existing helper pattern in this class
    Assert.Null(request);
    Assert.Equal("Unknown request.", error);
}

[Fact]
public void Settings_written_by_a_service_build_still_load()   // SettingsStoreTests
{
    File.WriteAllText(_store.FilePath, """{"schemaVersion":1,"runAsService":true,"controlUserSids":["S-1-5-21-1"],"httpPort":8080}""");
    Assert.Equal(8080, _store.Load().HttpPort);
    Assert.Null(_store.LastLoadWarning);
}
```

- [ ] **Step 2: Run them.** Expected: the verb tests FAIL (the verbs are still known); the settings test may already pass. That's fine: it guards the removal.
- [ ] **Step 3: Remove the feature.** Delete the listed files and the four request records. Have `RequestValidator.Read` map a `NotSupportedException` or `JsonException` from `ElevatedJson.Deserialize` to `"Unknown request."` if it does not already. Remove: the `--service` path and `UseWindowsService`; the service SID from the pipe ACL (keep the current user and Administrators); `WithGrants`; the Settings service section and "Secure this folder"; the packages `Microsoft.Extensions.Hosting.WindowsServices` and `System.ServiceProcess.ServiceController`. Delete `Microsoft.Extensions.Hosting` too if nothing else uses it.
- [ ] **Step 4: Run the Core and App suites.** Expected: PASS.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat!: remove the Windows service and its folder checks"`

### Task 3: Remove Let's Encrypt, certificate import and renewal

**Files:**
- Delete: `src/Anvil.Core/Certificates/{AcmeService,CertesAcmeProtocol,IAcmeProtocol,DnsCheck,RenewalPlanner,CertImporter,CertificateImportException}.cs`, `src/Anvil.Agent/RenewalWorker.cs`, and their tests (`CertImporterTests`, ACME / renewal / DNS-check tests)
- Modify: `src/Anvil.Core/Agent/{IAgentApi,AgentErrors}.cs`, `src/Anvil.Agent/{Orchestrator,AgentApi,AgentHost}.cs`, `src/Anvil.Core/Settings/AppSettings.cs` (drop `AcmeEmail`, `AcmeStaging`), `src/Anvil.Core/Paths/AppPaths.cs` (drop `Acme`), `src/Anvil.Core/Apache/ConfigRenderer.cs` and `Templates/anvil.conf.sbn` (drop the ACME challenge alias), `src/Anvil.App/ViewModels/{AddSiteViewModel,SitesViewModel,CertificateMessages}.cs` and views, `tests/Anvil.App.Tests/CertificateUiTests.cs` (keep only local-certificate cases), `Directory.Packages.props` (drop `Certes`)

**Interfaces:**
- Produces: `IAgentApi` without `ImportCertificateAsync`, `IssueLetsEncryptAsync`, `ResolveAsync`. `Orchestrator` constructor without the `acme` parameter. No expiry or renewal findings. `ReissueLocalCertificatesAsync` stays.

- [ ] **Step 1: Write the failing test** in `tests/Anvil.Core.Tests/Agent/SiteOperationsTests.cs`:

```csharp
[Fact]
public async Task Local_certificates_near_expiry_are_reissued_at_startup()
{
    // a LocalDev site whose stored certificate expires in 10 days (use the CertificateStore helpers this class already uses)
    var before = SeedLocalDevSite("shop.test", notAfter: Now.AddDays(10));
    await _orchestrator.InitializeAsync(CancellationToken.None);
    Assert.True(new CertificateStore(_paths).All()["shop.test"].NotAfter > before.NotAfter);
}
```

- [ ] **Step 2: Run it.** Expected: FAIL (nothing re-issues at startup).
- [ ] **Step 3: Remove and replace.** Delete the files and members listed. In `Orchestrator.InitializeAsync`, re-issue every enabled `LocalDev` site's certificate that expires within 30 days, using the existing `NeedsLocalCertificate` logic, before the first config render.
- [ ] **Step 4: Run the Core and App suites.** Expected: PASS.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat!: remove Let's Encrypt, certificate import and renewal"`

### Task 4: Slim the site model

**Files:**
- Modify: `src/Anvil.Core/Sites/{SiteDefinition,SiteSet,SiteStore,SiteValidator,HtdocsEntry}.cs`, `src/Anvil.Core/Apache/{AccessRules,ConfigRenderer}.cs`, `Templates/{vhost-site.conf.sbn,_proxy.sbn,_mount.sbn}`, `src/Anvil.Core/Certificates/CertificateStore.cs`, `src/Anvil.Agent/Orchestrator.cs`, `src/Anvil.Core/Agent/IAgentApi.cs`, `src/Anvil.App/ViewModels/{AddSiteViewModel,SitesViewModel}.cs`, `src/Anvil.App/Views/{AddSiteDialog,SitesView}.axaml`
- Create: `src/Anvil.Core/Sites/LegacySiteJson.cs`
- Delete: `src/Anvil.Core/Apache/HtpasswdFile.cs`, `Templates/_lb.sbn`, `src/Anvil.Core/Agent/SiteSecrets.cs`, `tests/Anvil.Integration.Tests/LoadBalancerTests.cs`, and the tests of removed fields; drop `BCrypt.Net-Next`
- Test: `tests/Anvil.Core.Tests/Sites/{SiteStoreTests,LegacySiteJsonTests}.cs`, `tests/Anvil.Core.Tests/Apache/AccessRulesTests.cs`, `tests/Anvil.App.Tests/AddSiteViewModelTests.cs`

**Interfaces:**
- Produces:
  - `enum SiteKind { Domain, Htdocs }`
  - `enum CertSource { None, LocalDev }`
  - `enum AccessLevel { ThisPc, Network }`
  - `SiteDefinition` without `Mounts`, `TrustedProxies`, `AcceptOnlyFromProxies`, `AllowedIps`, `PasswordProtected`; `Access` defaults to `AccessLevel.Network`
  - `AccessRules.Render(SiteDefinition s) -> string`
  - `IAgentApi.SaveSiteAsync(SiteDefinition s, CancellationToken ct = default)`
  - `LegacySiteJson.Upgrade(JsonObject site, string fileName) -> string?`: returns a warning and the site is skipped, or null with the object rewritten in place

- [ ] **Step 1: Write the failing tests.**

```csharp
public sealed class LegacySiteJsonTests
{
    private static JsonObject Site(string extra) => JsonNode.Parse($$"""{"id":"x","kind":"Domain","type":"Php","name":"x.test","root":"C:\\x"{{extra}}}""")!.AsObject();

    [Fact]
    public void Ip_site_is_skipped_with_a_warning()
    {
        var s = Site("") ; s["kind"] = "Ip";
        Assert.Equal("x.json: IP sites are no longer supported, so this site was skipped.", LegacySiteJson.Upgrade(s, "x.json"));
    }

    [Theory]
    [InlineData("Everyone", "Network")]
    [InlineData("SpecificIps", "ThisPc")]
    [InlineData("Network", "Network")]
    public void Access_maps_to_the_two_levels(string old, string now)
    {
        var s = Site($$""","access":"{{old}}","allowedIps":["10.0.0.5"]""");
        Assert.Null(LegacySiteJson.Upgrade(s, "x.json"));
        Assert.Equal(now, (string?)s["access"]);
        Assert.False(s.ContainsKey("allowedIps"));
    }

    [Theory]
    [InlineData("LetsEncrypt")] [InlineData("Imported")] [InlineData("LoadBalancer")]
    public void Other_certificate_sources_become_none(string old)
    {
        var s = Site($$""","cert":"{{old}}","trustedProxies":["10.0.0.1"],"passwordProtected":true,"mounts":[]""");
        Assert.Null(LegacySiteJson.Upgrade(s, "x.json"));
        Assert.Equal("None", (string?)s["cert"]);
        Assert.False(s.ContainsKey("trustedProxies") || s.ContainsKey("passwordProtected") || s.ContainsKey("mounts"));
    }
}

// AccessRulesTests
[Fact] public void This_pc_is_require_local() => Assert.Equal("Require local", AccessRules.Render(Site(AccessLevel.ThisPc)));
[Fact] public void Network_is_private_ranges() => Assert.Equal($"Require ip {ConfigRenderer.PrivateRanges}", AccessRules.Render(Site(AccessLevel.Network)));

// AddSiteViewModelTests
[Fact]
public void New_site_defaults_to_my_network_and_offers_two_levels()
{
    var vm = NewDialog();
    Assert.Equal(AccessLevel.Network, vm.Access);
    Assert.Equal([AccessLevel.ThisPc, AccessLevel.Network], vm.AccessChoices.Select(c => c.Level));
    Assert.Equal([CertSource.None, CertSource.LocalDev], vm.CertChoices.Select(c => c.Source));
}
```

(Use the names `AccessChoices` / `CertChoices` if the view model has no list properties yet; match whatever the dialog already binds to and assert the same sets.)

- [ ] **Step 2: Run them.** Expected: FAIL (compile errors on the new members, or the old values are still present).
- [ ] **Step 3: Implement.** Shrink the enums and the record. Call `SiteStore.All()` → `LegacySiteJson.Upgrade` on each file's `JsonObject` before deserializing. A returned warning goes to `Warnings` and the site is skipped; a rewritten object is used, but its file is not rewritten. Remove the IP-site, mount, load-balancer and password branches from the renderer, the validator, `SiteSet.IdFor` / `RequiredPhpVersions`, the Orchestrator (`Prepare`, `CertNames`, `data\auth` handling) and the dialog: the dialog has no kind picker, two access choices and two HTTPS choices. Delete any template or helper left with no caller (keep `_mount.sbn` if htdocs entries still render through it).
- [ ] **Step 4: Run the Core, App and Integration suites.** Expected: PASS.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat!: sites are domain sites and htdocs folders, open to this PC or the network"`

### Task 5: Development settings replace the mode switch

**Files:**
- Create: `src/Anvil.Core/Settings/DevelopmentDefaults.cs`, `src/Anvil.Core/Apache/Templates/defaults.conf.sbn` (from `mode.conf.sbn`, no security-header block)
- Delete: `src/Anvil.Core/Modes/*`, `src/Anvil.Agent/Orchestrator.Mode.cs`, `src/Anvil.App/ViewModels/{ModeViewModel,ModeSwitchViewModel}.cs`, `src/Anvil.App/Views/{ModeView,ModeSwitchDialog}.axaml(.cs)`, `src/Anvil.Core/Preflight/ProductionChecks.cs`, `Templates/mode.conf.sbn`, their tests (`ModeServiceTests`, `ModeViewModelTests`, `ProductionChecksTests`, profile tests); `src/Anvil.Core/IO/BackupService.cs` if only modes used it
- Modify: `src/Anvil.Core/Php/{PhpIniBootstrap,PhpExtensionManager}.cs`, `src/Anvil.Core/Database/MyIni.cs`, `src/Anvil.Core/Apache/{ConfigRenderer,RenderInput}.cs`, `Templates/{anvil.conf.sbn,00-catchall.conf.sbn}`, `src/Anvil.Core/Settings/AppSettings.cs`, `src/Anvil.Core/Agent/{IAgentApi,AgentStatus}.cs`, `src/Anvil.Core/Components/ComponentInstaller.cs` (+ `ComponentKind`), `src/Anvil.Core/Paths/AppPaths.cs` (drop `Profiles`), `src/Anvil.Agent/{Orchestrator,AgentApi}.cs`, `src/Anvil.App/Shell/{AppPage,ShellViewModel}.cs`, `src/Anvil.App/ViewModels/{DashboardViewModel,PhpViewModel}.cs` and views
- Test: `tests/Anvil.Core.Tests/Php/{PhpIniBootstrapTests,PhpExtensionManagerTests}.cs`, `tests/Anvil.Core.Tests/Database/MyIniTests.cs`, `tests/Anvil.Core.Tests/Apache/ConfigRendererTests.cs`, `tests/Anvil.Core.Tests/Agent/OrchestratorTests.cs`

**Interfaces:**
- Produces:
  - `static class DevelopmentDefaults` with
    - `IReadOnlyDictionary<string,string> Php`: today's `BuiltInProfiles.Development.Php`, verbatim
    - `IReadOnlyDictionary<string,string> Apache`: Timeout 300, RequestReadTimeout `header=0 body=0`, LimitRequestBody 536870912, ServerTokens Full, ServerSignature On, TraceEnable On, LogLevel info, PhpBackendTimeout 300
    - `IReadOnlyList<KeyValuePair<string,string>> Database(DbEngine e)`: today's development database values, verbatim
  - `PhpExtension(string Name, bool Enabled, bool Available, bool IsZend)`: no `ManagedByMode`
  - `static string? PhpExtensionManager.XdebugDll(IEnumerable<InstalledComponent> installed, string phpVersion)`: moved from `PhpModeIniRenderer`
  - `AgentStatus(IReadOnlyList<ServiceStatus> Services, IReadOnlyList<PreflightFinding> Findings, string AgentVersion)`
  - No `ServerMode`, `XdebugInProduction`, `RenderInput.Profile`, mode API methods, `AppPage.Mode`, `ComponentKind.ModSecurity` or `ComponentKind.CoreRuleSet`
  - Generated file `data\conf\defaults.conf` replaces `mode.conf`

- [ ] **Step 1: Write the failing tests.**

```csharp
// PhpIniBootstrapTests
[Fact]
public void New_ini_carries_development_values()
{
    _bootstrap.EnsureIni("8.5.12");
    var ini = IniDocument.Parse(File.ReadAllText(IniPath("8.5.12")));
    Assert.Equal("1G", ini.Get("PHP", "memory_limit"));
    Assert.Equal("On", ini.Get("PHP", "display_errors"));
    Assert.Equal("300", ini.Get("PHP", "max_execution_time"));
    Assert.Equal("512M", ini.Get("PHP", "upload_max_filesize"));
}

[Fact]
public void User_edit_survives_later_starts()
{
    _bootstrap.EnsureIni("8.5.12");
    SetIni("8.5.12", "PHP", "memory_limit", "2G");
    _bootstrap.EnsureIni("8.5.12");
    Assert.Equal("2G", ReadIni("8.5.12", "PHP", "memory_limit"));
}

[Fact]
public void Old_mode_overlay_is_deleted_but_a_user_file_is_kept()
{
    var overlay = Path.Combine(ConfD("8.5.12"), "99-arbipache-mode.ini");   // legacy-name
    File.WriteAllText(overlay, "; Written by ArbiPache for Development mode (PHP 8.5.12).\r\n");   // legacy-name
    _bootstrap.EnsureIni("8.5.12");
    Assert.False(File.Exists(overlay));

    File.WriteAllText(overlay, "; mine\r\nmemory_limit = 4G\r\n");
    _bootstrap.EnsureIni("8.5.12");
    Assert.True(File.Exists(overlay));
}

// MyIniTests
[Fact]
public void New_my_ini_carries_development_values_and_no_include()
{
    _myIni.EnsureCreated(DbEngine.MariaDb, 3306);
    var text = File.ReadAllText(_myIni.PathFor(DbEngine.MariaDb));
    Assert.Contains("wait_timeout=28800", text.Replace(" ", ""));
    Assert.Contains("max_statement_time=0", text.Replace(" ", ""));
    Assert.DoesNotContain("!include", text);
}

// PhpExtensionManagerTests
[Fact]
public void Xdebug_is_an_ordinary_extension()
{
    InstallXdebugFor("8.5");   // places a DLL under runtime\xdebug\<v>-php8.5 and records it as installed
    _manager.Set("8.5.12", "xdebug", enabled: true);
    var ini = File.ReadAllText(IniPath("8.5.12"));
    Assert.Contains("zend_extension=", ini);
    Assert.Contains("xdebug.mode = debug,develop", ini);
    Assert.True(_manager.List("8.5.12").Single(e => e.Name == "xdebug").Enabled);
    _manager.Set("8.5.12", "xdebug", enabled: false);
    Assert.DoesNotContain("zend_extension=", File.ReadAllText(IniPath("8.5.12")));
}

// ConfigRendererTests
[Fact]
public void Defaults_conf_has_development_values_and_no_security_headers()
{
    var conf = Render(new RenderInput { Settings = new AppSettings(), Stock = Stock }).Files["defaults.conf"];
    Assert.Contains("Timeout 300", conf);
    Assert.Contains("LogLevel info", conf);
    Assert.DoesNotContain("X-Frame-Options", conf);
}

// OrchestratorTests
[Fact]
public async Task Installing_xdebug_turns_it_on_for_that_php_branch()
{
    await InstallPhpAsync("8.5.12");
    await _orchestrator.InstallAsync(XdebugRelease(appliesTo: "8.5"), CancellationToken.None);
    Assert.True(_orchestrator.GetExtensions("8.5.12").Single(e => e.Name == "xdebug").Enabled);
}
```

- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
  - `PhpIniBootstrap.EnsureIni` writes `DevelopmentDefaults.Php` into `[PHP]` only when it creates `php.ini`. On every call it deletes `conf.d\99-arbipache-mode.ini` when its first line starts with `; Written by ArbiPache`, keeping the literal and tagging the line with `// legacy-name`.
  - `MyIni.EnsureCreated` writes `DevelopmentDefaults.Database(e)` under `[mysqld]` on creation. It drops a `!include …/mode.cnf` line, and deletes `mode.cnf` when that file carries the old generated header (`// legacy-name`).
  - `PhpExtensionManager.Set(v, "xdebug", on)` writes or removes an `[xdebug]` section in `php.ini`: `zend_extension="<dll, forward slashes>"`, `xdebug.mode = debug,develop`, `xdebug.start_with_request = trigger`.
  - `Orchestrator.InstallAsync` turns Xdebug on for each installed PHP of the build's minor, then restarts those pools.
  - The renderer always uses `Indexes FollowSymLinks` and the development catch-all, and renders `defaults.conf` from `DevelopmentDefaults.Apache`.
  - Delete everything listed and remove the Mode nav item and the Dashboard mode badge.
- [ ] **Step 4: Run the Core, App and Integration suites.** Expected: PASS.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat!: fixed development settings replace the Dev/Production switch"`

### Task 6: Firewall rules per program

**Files:**
- Modify: `src/Anvil.Core/Elevation/{FirewallRulePlanner,ElevatedRequest,RequestValidator}.cs`, `src/Anvil.Elevator/Handlers/FirewallHandler.cs`, `src/Anvil.Core/Settings/{AppSettings,SettingsStore}.cs`, `src/Anvil.Core/Agent/{ApplyResult,IAgentApi}.cs`, `src/Anvil.Agent/{Orchestrator,AgentApi}.cs`, `src/Anvil.App/Services/ElevationFollowUp.cs`, `src/Anvil.App/ViewModels/SettingsViewModel.cs`, `src/Anvil.Core/Apache/ConfigRenderer.cs`
- Create: `src/Anvil.Core/Elevation/WindowsFirewallReader.cs`
- Delete: `src/Anvil.Core/Elevation/FirewallScope.cs`
- Test: `tests/Anvil.Core.Tests/Elevation/{FirewallRulePlannerTests,RequestValidatorTests}.cs`, `tests/Anvil.Core.Tests/Settings/SettingsStoreTests.cs`, `tests/Anvil.App.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Produces:
  - `enum FirewallProtocol { Tcp = 6, Udp = 17 }`
  - `record FirewallRule(string Name, string Program, FirewallProtocol Protocol, string LocalPorts, int Profiles, string RemoteAddresses)`
  - `record ExistingFirewallRule(string Name, string? Program, int Protocol, string? LocalPorts, int Profiles, string? RemoteAddresses, bool Allow, bool Enabled)`
  - `record FirewallSetRequest(string? ApacheVersion, IReadOnlyList<int> Ports, bool NameService) : ElevatedRequest`, with `Equals` / `GetHashCode` overridden so `Ports` compares by sequence (tests and the Agent compare requests by value)
  - `static class FirewallRulePlanner`:
    - constants `NamePrefix = "Anvil "`, `WebServerRule = "Anvil web server"`, `NameServiceRule = "Anvil name service"`, `PrivateAndDomain = 3`
    - `IReadOnlyList<FirewallRule> Plan(FirewallSetRequest r, AppPaths paths)`
    - `FirewallSetRequest? Needed(AppSettings s, IReadOnlyList<SiteDefinition> sites)`
    - `bool IsOurs(ExistingFirewallRule rule, AppPaths paths)`
    - `bool Satisfied(IReadOnlyList<FirewallRule> planned, IReadOnlyList<ExistingFirewallRule> existing, AppPaths paths)`
  - `static IReadOnlyList<ExistingFirewallRule> WindowsFirewallReader.Read()` (COM `HNetCfg.FwPolicy2`, unelevated; an exception means an empty list plus a log line)
  - `NetworkAccess(bool HtdocsOnNetwork)`
  - `AppSettings.NetworkDomain` (string?, default null; set in Task 8, declared here so `Needed` compiles)
  - `ApplyResult.RequiredFirewall` is a `FirewallSetRequest?`
  - `IAgentApi.GetFirewallRequestAsync(CancellationToken ct = default) -> Task<FirewallSetRequest?>`
  - `ElevationFollowUp.FirewallAsync(FirewallSetRequest r, CancellationToken ct = default) -> Task<string?>` (message: `Windows did not let Anvil open the firewall ({error}). Other devices cannot reach this PC until it does.`) and `ElevationFollowUp.RunAsync(ApplyResult result, CancellationToken ct = default)` (no port parameters)
  - `AppSettings? SettingsStore.TryRead()`: never moves or writes anything (fixes M11)

- [ ] **Step 1: Write the failing tests** (replace the old planner tests):

```csharp
private static readonly AppPaths P = new(@"C:\Anvil");

[Fact]
public void Plans_web_server_and_name_service_rules() => Assert.Equal(
[
    new FirewallRule("Anvil web server", @"C:\Anvil\runtime\apache\2.4.69\bin\httpd.exe", FirewallProtocol.Tcp, "80,443", 3, "LocalSubnet"),
    new FirewallRule("Anvil name service", @"C:\Anvil\Anvil.Agent.exe", FirewallProtocol.Udp, "53", 3, "LocalSubnet"),
], FirewallRulePlanner.Plan(new FirewallSetRequest("2.4.69", [80, 443, 80], NameService: true), P));

[Fact]
public void Without_apache_there_is_no_web_rule() =>
    Assert.Equal(["Anvil name service"], FirewallRulePlanner.Plan(new FirewallSetRequest(null, [80], true), P).Select(r => r.Name));

[Fact]
public void Nothing_open_needs_nothing() =>
    Assert.Null(FirewallRulePlanner.Needed(new AppSettings { ActiveApache = "2.4.69", Network = new(false), NetworkDomain = "arbib.pc" }, [Site(AccessLevel.ThisPc)]));

[Fact]
public void Network_site_needs_the_web_ports_and_the_name_service_when_a_domain_is_set() =>
    Assert.Equal(new FirewallSetRequest("2.4.69", [80, 443], true),
        FirewallRulePlanner.Needed(new AppSettings { ActiveApache = "2.4.69", Network = new(false), NetworkDomain = "arbib.pc" }, [Site(AccessLevel.Network)]));

[Fact]
public void Requests_compare_ports_by_value() =>
    Assert.Equal(new FirewallSetRequest("2.4.69", [80, 443], false), new FirewallSetRequest("2.4.69", new List<int> { 80, 443 }, false));

[Theory]
[InlineData(@"C:\Anvil\runtime\apache\2.4.68\bin\httpd.exe", "Apache HTTP Server", true)]
[InlineData(@"c:\anvil\RUNTIME\apache\2.4.69\bin\HTTPD.EXE", "x", true)]
[InlineData(@"C:\Anvil\Anvil.Agent.exe", "x", true)]
[InlineData(null, "Anvil web server", true)]
[InlineData(@"C:\Other\httpd.exe", "Apache HTTP Server", false)]
public void Recognises_rules_for_its_own_programs(string? program, string name, bool ours) =>
    Assert.Equal(ours, FirewallRulePlanner.IsOurs(new ExistingFirewallRule(name, program, 6, "*", 3, "*", true, true), P));

[Fact]
public void Block_or_public_rule_for_its_program_is_not_satisfied()
{
    var planned = FirewallRulePlanner.Plan(new FirewallSetRequest("2.4.69", [80, 443], false), P);
    var exact = new ExistingFirewallRule("Anvil web server", planned[0].Program, 6, "80,443", 3, "LocalSubnet", true, true);
    Assert.True(FirewallRulePlanner.Satisfied(planned, [exact], P));
    Assert.False(FirewallRulePlanner.Satisfied(planned, [], P));
    Assert.False(FirewallRulePlanner.Satisfied(planned, [exact, exact with { Name = "Apache HTTP Server", Allow = false }], P));
    Assert.False(FirewallRulePlanner.Satisfied(planned, [exact, exact with { Name = "Apache HTTP Server", Profiles = 4 }], P));
    Assert.True(FirewallRulePlanner.Satisfied(planned, [exact, exact with { Name = "Apache HTTP Server", Allow = false, Enabled = false }], P));
}

// RequestValidatorTests
[Fact] public void Firewall_request_for_an_apache_that_is_not_installed_is_refused() =>
    Assert.Equal("Apache 2.4.70 is not installed.", Validate(new FirewallSetRequest("2.4.70", [80], false)));
[Fact] public void Firewall_request_with_a_path_in_the_version_is_refused() =>
    Assert.Equal("'..\\x' is not an Apache version.", Validate(new FirewallSetRequest(@"..\x", [80], false)));
[Fact]
public void Validator_never_moves_a_corrupt_settings_file()   // M11
{
    File.WriteAllText(SettingsFile, "{not json");
    Assert.Equal("Anvil's settings file cannot be read.", Validate(new FirewallSetRequest(null, [80], false)));
    Assert.True(File.Exists(SettingsFile));
}

// SettingsViewModelTests
[Fact]
public async Task Turning_htdocs_onto_the_network_asks_for_the_agents_firewall_request()
{
    _api.FirewallRequest = new FirewallSetRequest("2.4.69", [80, 443], false);
    _vm.HtdocsOnNetwork = true;
    await _vm.SaveNetworkCommand.ExecuteAsync(null);
    Assert.Equal(_api.FirewallRequest, Assert.IsType<FirewallSetRequest>(Assert.Single(_elevator.Requests)));
}
```

- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
  - `Plan` resolves paths from `AppPaths`: `runtime\apache\<v>\bin\httpd.exe` and `<Root>\Anvil.Agent.exe`. Ports are distinct and comma-joined in the order given; a port outside 1–65535 throws `ArgumentOutOfRangeException`.
  - `Needed` returns null unless htdocs is on the network or an enabled domain site has `Access == Network`. Otherwise it returns `(ActiveApache, [HttpPort, HttpsPort], NameService: NetworkDomain is not null)`.
  - `IsOurs` is true for any rule named with `NamePrefix`, any rule whose program is that exact Agent path, and any `httpd.exe` under `<Root>\runtime\apache\`, all compared case-insensitively.
  - `Satisfied` needs both of these:
    - every planned rule has an enabled allow rule matching name, program, protocol, ports, profiles and remote addresses, ignoring case and treating `LocalSubnet` as written;
    - no other enabled rule `IsOurs` is a block rule or includes the Public profile (4).
  - `FirewallHandler` removes every rule that `IsOurs` (it reads `ApplicationName`), then adds the planned rules with `ApplicationName`, `Protocol`, `LocalPorts`, `Profiles`, `RemoteAddresses`, `Grouping = "Anvil"` and the descriptions "Added by Anvil for its web server." / "Added by Anvil for its name service."
  - `RequestValidator` uses `SettingsStore.TryRead()` and checks:
    - the version against `^[0-9]+\.[0-9]+\.[0-9]+$`;
    - that `runtime\apache\<v>\bin\httpd.exe` exists;
    - that every port is one of the settings' ports.
  - Settings' htdocs switch and port change call `GetFirewallRequestAsync` and run the request through `ElevationFollowUp`.
  - The renderer drops `PrivateNetworksOnly` and always uses the private ranges.
- [ ] **Step 4: Run the Core and App suites.** Expected: PASS.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat: firewall rules for Anvil's own programs, local network only"`

### Task 7: LAN addresses and network category

**Files:**
- Create: `src/Anvil.Core/Network/{LanAddresses,NetworkCategory}.cs`
- Delete: `src/Anvil.App/Services/LanAddress.cs` (callers move to `LanAddresses`)
- Test: `tests/Anvil.Core.Tests/Network/LanAddressesTests.cs`

**Interfaces:**
- Produces:
  - `record AdapterInfo(string Id, string Name, string Description, NetworkInterfaceType Type, bool Up, bool HasGateway, IReadOnlyList<IPAddress> IPv4)`
  - `record LanAddress(string AdapterId, string AdapterName, IPAddress Address)`
  - `static class LanAddresses`:
    - `IReadOnlyList<LanAddress> Pick(IEnumerable<AdapterInfo> adapters)`
    - `IReadOnlyList<LanAddress> Find()`
    - `bool IsPrivate(IPAddress a)` (IPv4 10/8, 172.16/12, 192.168/16)
    - `bool IsVirtual(AdapterInfo a)`
  - `static IReadOnlySet<string> NetworkCategory.PublicAdapterIds()`: ids in `NetworkInterface.Id` form; any COM failure gives an empty set

- [ ] **Step 1: Write the failing test** with the owner's real adapter list:

```csharp
private static AdapterInfo A(string name, string desc, string ip, bool gateway, bool up = true, NetworkInterfaceType type = NetworkInterfaceType.Ethernet) =>
    new($"{{{name}}}", name, desc, type, up, gateway, [IPAddress.Parse(ip)]);

[Fact]
public void Picks_private_addresses_on_real_adapters_with_a_gateway()
{
    var picked = LanAddresses.Pick(
    [
        A("Ethernet", "Intel(R) Ethernet Connection", "192.168.100.254", gateway: true),
        A("vEthernet (WSL (Hyper-V firewall))", "Hyper-V Virtual Ethernet Adapter", "172.18.0.1", gateway: false),
        A("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter #2", "172.21.128.1", gateway: true),
        A("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", "192.168.108.1", gateway: true),
        A("Wi-Fi", "Wireless", "169.254.1.74", gateway: false, type: NetworkInterfaceType.Wireless80211),
        A("Ethernet 3", "USB LAN", "10.0.0.7", gateway: true, up: false),
        A("Ethernet 4", "Public card", "8.8.4.4", gateway: true),
    ]);
    Assert.Equal(["192.168.100.254"], picked.Select(p => p.Address.ToString()));
}
```

- [ ] **Step 2: Run it.** Expected: FAIL (type missing).
- [ ] **Step 3: Implement.**
  - `IsVirtual` is true for Loopback and Tunnel types, and for a name or description containing `Hyper-V`, `vEthernet`, `VMware`, `VirtualBox`, `WSL` or `Loopback` (ignoring case).
  - `Find` maps `NetworkInterface.GetAllNetworkInterfaces()` into `AdapterInfo` records. A gateway counts when it is not `0.0.0.0`.
  - `NetworkCategory` uses Network List Manager over late-bound COM (CLSID `DCB00C01-570F-4A9B-8D69-199FDBA5723B`): it walks `GetNetworkConnections()`, collects `GetAdapterId()` where `GetNetwork().GetCategory() == 0`, and formats each id as `"{GUID}"` in upper case.
- [ ] **Step 4: Run Core tests.** Expected: PASS.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat: find this PC's LAN addresses and Public networks"`

### Task 8: Network domain and network names

**Files:**
- Create: `src/Anvil.Core/Sites/NetworkNames.cs`, `src/Anvil.Core/Network/ColleagueSetup.cs`
- Modify: `src/Anvil.Agent/Orchestrator.cs` (`Validate` and `SaveSettingsAsync` normalize and check the domain)
- Test: `tests/Anvil.Core.Tests/Sites/NetworkNamesTests.cs`, `tests/Anvil.Core.Tests/Network/ColleagueSetupTests.cs`, `tests/Anvil.Core.Tests/Agent/OrchestratorTests.cs`

**Interfaces:**
- Produces:
  - `record NetworkName(string Label, string Host, string? SiteId, string? HtdocsFolder)`
  - `record NetworkNameIssue(string? SiteId, string? HtdocsFolder, string Reason)`
  - `record NetworkNamePlan(IReadOnlyList<NetworkName> Names, IReadOnlyList<NetworkNameIssue> Issues)` with `static NetworkNamePlan Empty`
  - `static class NetworkNames`:
    - `string? NormalizeDomain(string? input)`
    - `string? DomainError(string domain)`
    - `NetworkNamePlan Plan(string? networkDomain, IReadOnlyList<SiteDefinition> sites, IReadOnlyList<HtdocsEntry> htdocs, bool htdocsOnNetwork)`
  - `static class ColleagueSetup`:
    - `string WindowsCommand(string domain, IPAddress ip)`
    - `string MacCommand(string domain, IPAddress ip)`
    - `string WindowsUndoCommand(string domain)`

- [ ] **Step 1: Write the failing tests.**

```csharp
[Theory]
[InlineData(" .Arbib.PC. ", "arbib.pc")]
[InlineData("   ", null)]
[InlineData(null, null)]
public void Domain_is_normalized(string? input, string? expected) => Assert.Equal(expected, NetworkNames.NormalizeDomain(input));

[Theory]
[InlineData("arbib.pc", null)]
[InlineData("pc", "A network domain needs at least two parts, such as arbib.pc.")]
[InlineData("dev.localhost", "A network domain cannot be localhost or end in .localhost.")]
[InlineData("arbib_x.pc", "'arbib_x' is not allowed in a network domain: use the letters a to z, digits and hyphens.")]
[InlineData("a..pc", "'' is not allowed in a network domain: use the letters a to z, digits and hyphens.")]
public void Domain_errors(string domain, string? error) => Assert.Equal(error, NetworkNames.DomainError(domain));

[Fact]
public void Network_site_answers_at_its_first_label()
{
    var plan = NetworkNames.Plan("arbib.pc", [Site("shop.test", AccessLevel.Network)], [], htdocsOnNetwork: true);
    Assert.Equal(new NetworkName("shop", "shop.arbib.pc", "shop.test", null), Assert.Single(plan.Names));
}

[Fact]
public void This_pc_and_disabled_sites_get_no_name() =>
    Assert.Empty(NetworkNames.Plan("arbib.pc", [Site("a.test", AccessLevel.ThisPc), Site("b.test", AccessLevel.Network) with { Enabled = false }], [], true).Names);

[Fact]
public void Folder_names_are_lower_cased_or_reported()
{
    var plan = NetworkNames.Plan("arbib.pc", [], [Entry("MyApp"), Entry("my app"), Entry("café"), Entry("old_site")], htdocsOnNetwork: true);
    Assert.Equal(["myapp.arbib.pc"], plan.Names.Select(n => n.Host));
    Assert.Equal("my app has no network name: a network name may use only the letters a to z, digits and hyphens.",
        plan.Issues.Single(i => i.HtdocsFolder == "my app").Reason);
    Assert.Equal(3, plan.Issues.Count);
}

[Fact]
public void Htdocs_off_the_network_gets_no_names() => Assert.Empty(NetworkNames.Plan("arbib.pc", [], [Entry("blog")], htdocsOnNetwork: false).Names);

[Fact]
public void Site_beats_folder_and_older_site_beats_newer()
{
    var older = Site("shop.test", AccessLevel.Network) with { Created = T0 };
    var newer = Site("shop.local", AccessLevel.Network) with { Created = T0.AddDays(1) };
    var plan = NetworkNames.Plan("arbib.pc", [newer, older], [Entry("shop")], true);
    Assert.Equal("shop.test", Assert.Single(plan.Names).SiteId);
    Assert.Equal("shop.arbib.pc already belongs to shop.test.", plan.Issues.Single(i => i.SiteId == newer.Id).Reason);
    Assert.Equal("shop.arbib.pc already belongs to shop.test.", plan.Issues.Single(i => i.HtdocsFolder == "shop").Reason);
}

[Fact]
public void No_domain_no_names() => Assert.Same(NetworkNamePlan.Empty, NetworkNames.Plan(null, [Site("a.test", AccessLevel.Network)], [], true));

// ColleagueSetupTests
[Fact] public void Windows_command() => Assert.Equal("Add-DnsClientNrptRule -Namespace \".arbib.pc\" -NameServers 192.168.1.5",
    ColleagueSetup.WindowsCommand("arbib.pc", IPAddress.Parse("192.168.1.5")));
[Fact] public void Mac_command() => Assert.Equal("sudo mkdir -p /etc/resolver && echo \"nameserver 192.168.1.5\" | sudo tee /etc/resolver/arbib.pc",
    ColleagueSetup.MacCommand("arbib.pc", IPAddress.Parse("192.168.1.5")));
[Fact] public void Windows_undo_command() => Assert.Equal("Get-DnsClientNrptRule | Where-Object Namespace -eq '.arbib.pc' | Remove-DnsClientNrptRule -Force",
    ColleagueSetup.WindowsUndoCommand("arbib.pc"));

// OrchestratorTests
[Fact]
public async Task Saving_settings_normalizes_and_checks_the_network_domain()
{
    Assert.True((await _orchestrator.SaveSettingsAsync(_orchestrator.Settings with { NetworkDomain = " Arbib.PC " }, default)).Ok);
    Assert.Equal("arbib.pc", _orchestrator.Settings.NetworkDomain);
    var bad = await _orchestrator.SaveSettingsAsync(_orchestrator.Settings with { NetworkDomain = "pc" }, default);
    Assert.Equal("A network domain needs at least two parts, such as arbib.pc.", bad.Error);
}
```

- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
  - A label is valid when it matches `^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$` after lower-casing (invariant culture).
  - The whole domain must be at most 253 characters, and is checked label by label.
  - A site's label is the first label of `Name`.
  - Conflicts are resolved in this order: sites ordered by `Created` (then by `Id`), then htdocs entries by folder.
  - The winner's display name is the site's `Name`, or `htdocs\<folder>` for a folder.
- [ ] **Step 4: Run Core tests.** Expected: PASS.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat: network domain setting and the names each site gets"`

### Task 9: Render network names in Apache

**Files:**
- Create: `src/Anvil.Core/Apache/Templates/vhost-network-htdocs.conf.sbn`
- Modify: `src/Anvil.Core/Apache/ConfigRenderer.cs` (`RenderSite` gains `string? networkHost = null`)
- Test: `tests/Anvil.Core.Tests/Apache/VhostRenderTests.cs` (+ two snapshots under `Snapshots\`)

**Interfaces:**
- Consumes: `NetworkNames.Plan` (Task 8)
- Produces: files `vhosts\net-<label>.conf`, one per `NetworkName`. The bare network domain needs no file, because the catch-all (`00-catchall.conf`) already answers it.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact]
public void Network_name_for_a_site_is_plain_http_without_redirect()
{
    var site = Site("shop.test", AccessLevel.Network) with { Cert = CertSource.LocalDev, HttpRedirect = true, Aliases = ["www.shop.test"] };
    var files = Render(Input(site, networkDomain: "arbib.pc", withLocalCert: true)).Files;
    var net = files[Path.Combine("vhosts", "net-shop.conf")];
    Assert.Contains("ServerName shop.arbib.pc", net);
    Assert.Contains($"Require ip {ConfigRenderer.PrivateRanges}", net);
    Assert.DoesNotContain("Redirect", net);
    Assert.DoesNotContain("SSLEngine", net);
    Assert.DoesNotContain("ServerAlias", net);
    Snapshot.Match("network-site", net);
}

[Fact]
public void Network_name_for_an_htdocs_folder_serves_that_folder()
{
    var net = Render(Input(htdocs: [Entry("blog")], networkDomain: "arbib.pc", htdocsOnNetwork: true)).Files[Path.Combine("vhosts", "net-blog.conf")];
    Assert.Contains("ServerName blog.arbib.pc", net);
    Assert.Contains($"DocumentRoot \"{AppPaths.ForApache(Path.Combine(HtdocsDir, "blog"))}\"", net);
    Assert.Contains("SetHandler \"proxy:fcgi://127.0.0.1:", net);
    Snapshot.Match("network-htdocs", net);
}

[Fact]
public void Folder_without_network_name_renders_no_network_file() =>
    Assert.DoesNotContain(Render(Input(htdocs: [Entry("my app")], networkDomain: "arbib.pc", htdocsOnNetwork: true)).Files.Keys, k => k.Contains("net-"));

[Fact]
public void No_network_domain_renders_no_network_files() =>
    Assert.DoesNotContain(Render(Input(Site("shop.test", AccessLevel.Network))).Files.Keys, k => k.Contains("net-"));
```

- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
  - A site network name calls `RenderSite(site, …, networkHost: host)`. That renders only the HTTP vhost, with `ServerName host`, no `ServerAlias`, no redirect and the site's access rules, logging to the site's own log folder.
  - An htdocs name renders the new template: `<VirtualHost *:{port}>` with `ServerName`, `DocumentRoot`, a `<Directory>` (`Options Indexes FollowSymLinks`, `AllowOverride All`, `Require ip <PrivateRanges>`), the entry's PHP handler (`PhpHandler(PoolFor(entry.PhpVersion …))`) and the htdocs log.
  - `net-` files sort after `00-`/`01-` and before `site-`; add a test comment saying why the order matters.
- [ ] **Step 4: Run Core tests.** Expected: PASS. Then run `ApacheConfigTests` in Integration to see real `httpd -t` accept the output: `dotnet test --project tests/Anvil.Integration.Tests -- --filter-class "*ApacheConfigTests"` after adding one case there, `Network_names_pass_the_config_test`.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat: sites and htdocs folders answer at their network names"`

### Task 10: DNS message codec and answer policy

**Files:**
- Create: `src/Anvil.Core/Network/{DnsMessage,DnsPolicy}.cs`
- Test: `tests/Anvil.Core.Tests/Network/{DnsMessageTests,DnsPolicyTests}.cs`, `tests/Anvil.Core.Tests/Network/DnsPackets.cs` (test helper that builds query bytes)

**Interfaces:**
- Produces:
  - `enum DnsReplyCode : byte { NoError = 0, Refused = 5 }`
  - `record DnsQuestion(ushort Id, bool RecursionDesired, string Name, ushort Type, ushort Class, ReadOnlyMemory<byte> RawQuestion)`: `Name` lower-cased, no trailing dot; `RawQuestion` = the question section's exact bytes
  - `static class DnsMessage` with constants `TypeA = 1`, `TypeAaaa = 28`, `ClassIn = 1`, and
    - `DnsQuestion? TryParse(ReadOnlySpan<byte> packet)`
    - `byte[] Reply(DnsQuestion q, DnsReplyCode code, IPAddress? address, uint ttl)`
  - `static class DnsPolicy` with `const uint Ttl = 60` and `(DnsReplyCode Code, bool WithAddress) Decide(DnsQuestion q, string networkDomain)`

- [ ] **Step 1: Write the failing tests.**

```csharp
// DnsMessageTests
[Fact]
public void Parses_a_plain_query()
{
    var q = DnsMessage.TryParse(DnsPackets.Query(0x1234, "shop.arbib.pc", DnsMessage.TypeA))!;
    Assert.Equal((ushort)0x1234, q.Id);
    Assert.Equal("shop.arbib.pc", q.Name);
    Assert.True(q.RecursionDesired);
}

[Fact]
public void Reply_with_address_has_one_authoritative_answer()
{
    var q = DnsMessage.TryParse(DnsPackets.Query(7, "shop.arbib.pc", DnsMessage.TypeA))!;
    var r = DnsMessage.Reply(q, DnsReplyCode.NoError, IPAddress.Parse("192.168.100.254"), 60);
    Assert.Equal(new byte[] { 0x00, 0x07, 0x85, 0x00, 0, 1, 0, 1, 0, 0, 0, 0 }, r[..12]);   // QR AA RD, NOERROR, QD=1 AN=1
    Assert.Equal(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 192, 168, 100, 254 }, r[^16..]);
}

[Fact]
public void Refused_reply_has_no_answer()
{
    var r = DnsMessage.Reply(DnsMessage.TryParse(DnsPackets.Query(7, "example.com", DnsMessage.TypeA))!, DnsReplyCode.Refused, null, 60);
    Assert.Equal(0x05, r[3] & 0x0F);
    Assert.Equal(0, r[7]);
}

[Fact]
public void Mixed_case_query_is_answered_and_echoed_exactly()
{
    var packet = DnsPackets.Query(9, "ShOp.ArBiB.pC", DnsMessage.TypeA);
    var q = DnsMessage.TryParse(packet)!;
    Assert.Equal("shop.arbib.pc", q.Name);
    var r = DnsMessage.Reply(q, DnsReplyCode.NoError, IPAddress.Loopback, 60);
    Assert.Equal(packet.AsSpan(12, q.RawQuestion.Length).ToArray(), r.AsSpan(12, q.RawQuestion.Length).ToArray());
}

[Fact]
public void Query_with_edns_record_is_answered()
{
    var q = DnsMessage.TryParse(DnsPackets.Query(3, "shop.arbib.pc", DnsMessage.TypeA, edns: true))!;
    var r = DnsMessage.Reply(q, DnsReplyCode.NoError, IPAddress.Loopback, 60);
    Assert.Equal(0, r[11]);   // ARCOUNT 0
}

[Theory]
[MemberData(nameof(Malformed))]   // empty, 11 bytes, QR=1, opcode 2, QDCOUNT 0, QDCOUNT 2, label 64 long, compression pointer in the question, name > 255, truncated question
public void Malformed_packets_are_ignored(byte[] packet) => Assert.Null(DnsMessage.TryParse(packet));

// DnsPolicyTests
[Theory]
[InlineData("shop.arbib.pc", DnsMessage.TypeA, DnsMessage.ClassIn, DnsReplyCode.NoError, true)]
[InlineData("arbib.pc", DnsMessage.TypeA, DnsMessage.ClassIn, DnsReplyCode.NoError, true)]
[InlineData("never.added.arbib.pc", DnsMessage.TypeA, DnsMessage.ClassIn, DnsReplyCode.NoError, true)]
[InlineData("shop.arbib.pc", DnsMessage.TypeAaaa, DnsMessage.ClassIn, DnsReplyCode.NoError, false)]
[InlineData("notarbib.pc", DnsMessage.TypeA, DnsMessage.ClassIn, DnsReplyCode.Refused, false)]
[InlineData("example.com", DnsMessage.TypeA, DnsMessage.ClassIn, DnsReplyCode.Refused, false)]
[InlineData("shop.arbib.pc", DnsMessage.TypeA, (ushort)3, DnsReplyCode.Refused, false)]
public void Decides(string name, ushort type, ushort cls, DnsReplyCode code, bool withAddress) =>
    Assert.Equal((code, withAddress), DnsPolicy.Decide(new DnsQuestion(1, true, name, type, cls, Array.Empty<byte>()), "arbib.pc"));
```

- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
  - `TryParse` accepts a packet only when all of these hold: at least 12 header bytes; QR = 0; opcode 0; QDCOUNT = 1. Any AN, NS and AR counts are allowed and ignored.
  - Within the question name: labels are 1–63 bytes, a byte ≥ 0xC0 (compression) is refused, the total name is ≤ 255 bytes, and four bytes of type and class must follow.
  - `Reply`:
    - Copies the id.
    - Sets the flags to `0x8000 | 0x0400 | (rd ? 0x0100 : 0) | code`.
    - Sets QD = 1, AN = (address is null ? 0 : 1), NS = 0, AR = 0.
    - Copies `RawQuestion` unchanged.
    - When there is an address, appends `C0 0C 00 01 00 01 <ttl> 00 04 <4 bytes>`.
  - `Decide` returns:
    - `(Refused, false)` unless the class is IN and the name equals the domain or ends with `"." + domain`;
    - `(NoError, true)` for type A;
    - `(NoError, false)` for every other type.
- [ ] **Step 4: Run Core tests.** Expected: PASS.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat: DNS message parsing and answers for the network domain"`

### Task 11: DNS responder

**Files:**
- Create: `src/Anvil.Core/Network/{DnsResponder,INameService}.cs`
- Test: `tests/Anvil.Core.Tests/Network/DnsResponderTests.cs`

**Interfaces:**
- Consumes: `DnsMessage`, `DnsPolicy` (Task 10), `LanAddresses.IsPrivate` (Task 7)
- Produces:
  - `record DnsBindFailure(IPAddress Address, string Reason)`
  - `interface INameService { Task<IReadOnlyList<DnsBindFailure>> ApplyAsync(string? networkDomain, IReadOnlyList<IPAddress> addresses, CancellationToken ct); IReadOnlyList<IPAddress> Listening { get; } }`
  - `sealed class DnsResponder(int port = 53) : INameService, IAsyncDisposable` with `static bool Accepts(IPAddress asker)` (loopback or `LanAddresses.IsPrivate`)

- [ ] **Step 1: Write the failing tests** (real sockets on loopback, a free port picked per test):

```csharp
[Fact]
public async Task Answers_with_the_address_it_listens_on()
{
    await using var dns = new DnsResponder(_port);
    Assert.Empty(await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback], default));
    var reply = await AskAsync(IPAddress.Loopback, "shop.arbib.pc");
    Assert.Equal(new byte[] { 127, 0, 0, 1 }, reply[^4..]);
}

[Fact]
public async Task Refuses_names_outside_the_domain()
{
    await using var dns = new DnsResponder(_port);
    await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback], default);
    Assert.Equal(5, (await AskAsync(IPAddress.Loopback, "example.com"))[3] & 0x0F);
}

[Fact] public void Accepts_only_private_askers()
{
    Assert.True(DnsResponder.Accepts(IPAddress.Parse("192.168.1.20")));
    Assert.True(DnsResponder.Accepts(IPAddress.Loopback));
    Assert.False(DnsResponder.Accepts(IPAddress.Parse("8.8.8.8")));
}

[Fact]
public async Task Malformed_packet_does_not_stop_it()
{
    await using var dns = new DnsResponder(_port);
    await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback], default);
    await SendRawAsync(IPAddress.Loopback, [1, 2, 3]);
    Assert.Equal(1, (await AskAsync(IPAddress.Loopback, "shop.arbib.pc"))[7]);
}

[Fact]
public async Task Client_that_vanished_does_not_stop_it()
{
    await using var dns = new DnsResponder(_port);
    await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback], default);
    using (var gone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        await gone.SendAsync(DnsPackets.Query(1, "shop.arbib.pc", DnsMessage.TypeA), new IPEndPoint(IPAddress.Loopback, _port));
    await Task.Delay(200);   // the reply hits a closed port; Windows reports it back to the listener
    Assert.Equal(1, (await AskAsync(IPAddress.Loopback, "shop.arbib.pc"))[7]);
}

[Fact]
public async Task Busy_address_is_reported_and_others_answer()
{
    var second = IPAddress.Parse("127.0.0.2");
    using var squatter = new UdpClient { ExclusiveAddressUse = true };
    squatter.Client.Bind(new IPEndPoint(IPAddress.Loopback, _port));
    await using var dns = new DnsResponder(_port);
    var failures = await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback, second], default);
    Assert.Equal(IPAddress.Loopback, Assert.Single(failures).Address);
    Assert.Equal(new byte[] { 127, 0, 0, 2 }, (await AskAsync(second, "shop.arbib.pc"))[^4..]);
}

[Fact]
public async Task Moves_to_new_addresses_and_stops_without_a_domain()
{
    await using var dns = new DnsResponder(_port);
    await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback], default);
    await dns.ApplyAsync("arbib.pc", [IPAddress.Parse("127.0.0.2")], default);
    Assert.Equal([IPAddress.Parse("127.0.0.2")], dns.Listening);
    await dns.ApplyAsync(null, [IPAddress.Parse("127.0.0.2")], default);
    Assert.Empty(dns.Listening);
    using var rebind = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), _port));   // the port is free again
}
```

- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
  - One `Socket` (UDP, `ExclusiveAddressUse = true`) per address, bound to `(address, port)`.
  - Disable `SIO_UDP_CONNRESET` (`socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null)`), so an ICMP "port unreachable" does not end `ReceiveFromAsync`.
  - Each socket runs a receive loop. A sender `Accepts` rejects is dropped. Parse; null means drop. `DnsPolicy.Decide` against the current domain (held in a volatile field, so a domain change needs no rebind). Reply with that socket's own address.
  - Exceptions inside one iteration are logged and the loop continues; only cancellation ends it.
  - `ApplyAsync` closes sockets no longer listed, opens new ones, and returns the bind failures with the exception message as `Reason`. A null domain or an empty list closes everything.
- [ ] **Step 4: Run Core tests.** Expected: PASS.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat: name service answering the network domain on LAN addresses"`

### Task 12: Agent network wiring

**Files:**
- Create: `src/Anvil.Core/Network/NetworkFindings.cs`
- Modify: `src/Anvil.Agent/{Orchestrator,AgentHost,AgentApi}.cs`, `src/Anvil.Core/Agent/{IAgentApi,AgentStatus}.cs`, `tests/Anvil.Core.Tests/Agent/AgentFakes.cs`, `tests/Anvil.App.Tests/Support/FakeAgentApi.cs`
- Test: `tests/Anvil.Core.Tests/Network/NetworkFindingsTests.cs`, `tests/Anvil.Core.Tests/Agent/NetworkOperationsTests.cs`

**Interfaces:**
- Consumes: Tasks 6–11
- Produces:
  - `static class NetworkFindings`:
    - constants `FirewallId = "firewall-missing"`, `PublicId = "network-public"`, `DnsBindPrefix = "dns-bind-"`
    - `IReadOnlyList<PreflightFinding> For(bool anyOpen, bool firewallReady, IReadOnlyList<LanAddress> publicAddresses, IReadOnlyList<DnsBindFailure> bindFailures)`
  - `AgentStatus(IReadOnlyList<ServiceStatus> Services, IReadOnlyList<PreflightFinding> Findings, string AgentVersion, IReadOnlyList<string> NameServiceAddresses)`
  - `IAgentApi.RefreshNetworkAsync(CancellationToken ct = default) -> Task`
  - `Orchestrator`:
    - optional constructor parameters `INameService? names = null`, `Func<IReadOnlyList<LanAddress>>? lanAddresses = null`, `Func<IReadOnlySet<string>>? publicAdapters = null`, `Func<IReadOnlyList<ExistingFirewallRule>>? firewallRules = null`. Null means empty or no-op: tests never touch the system.
    - `public Task RefreshNetworkAsync(CancellationToken ct)`
    - `public FirewallSetRequest? FirewallRequest()`
  - `AgentHost` passes `new DnsResponder()`, `LanAddresses.Find`, `NetworkCategory.PublicAdapterIds` and `WindowsFirewallReader.Read`, and calls `RefreshNetworkAsync` on `NetworkChange.NetworkAddressChanged`, debounced to 2 seconds.

- [ ] **Step 1: Write the failing tests.**

```csharp
// NetworkFindingsTests
[Fact] public void Nothing_open_nothing_to_say() =>
    Assert.Empty(NetworkFindings.For(anyOpen: false, firewallReady: false, [Lan("Ethernet")], []));

[Fact]
public void Missing_rules_and_public_network_are_reported()
{
    var f = NetworkFindings.For(true, false, [Lan("Ethernet")], []);
    Assert.Equal(new PreflightFinding("firewall-missing", Severity.Warning,
        "Other devices cannot reach this PC yet: Windows Firewall does not let them in.", "Open the firewall for Anvil (Windows asks for permission)."), f[0]);
    Assert.Equal(new PreflightFinding("network-public", Severity.Warning,
        "Windows treats this network as Public, so other devices are blocked (Ethernet).", "Switch the network to Private in Windows' network settings."), f[1]);
}

[Fact]
public void Busy_port_53_is_reported_per_address() =>
    Assert.Equal(new PreflightFinding("dns-bind-192.168.100.254", Severity.Warning,
        "Network names are off on 192.168.100.254: port 53 is in use there.", "Sites still open at http://192.168.100.254/<folder>."),
        Assert.Single(NetworkFindings.For(true, true, [], [new DnsBindFailure(IPAddress.Parse("192.168.100.254"), "in use")])));

// NetworkOperationsTests (fakes: FakeNameService records calls; lan/public/firewall seams are lambdas)
[Fact]
public async Task Starting_the_web_server_starts_the_name_service()
{
    Settings(s => s with { NetworkDomain = "arbib.pc" });
    await _o.StartAsync(ServiceId.Apache, default);
    Assert.Equal("arbib.pc", _names.LastCall.Domain);
    Assert.Equal([Ip("192.168.100.254")], _names.LastCall.Addresses);
}

[Fact]
public async Task Stopping_the_web_server_stops_the_name_service()
{
    Settings(s => s with { NetworkDomain = "arbib.pc" });
    await _o.StartAsync(ServiceId.Apache, default);
    await _o.StopAsync(ServiceId.Apache, default);
    Assert.Null(_names.LastCall.Domain);
}

[Fact]
public async Task Address_change_moves_the_name_service()
{
    Settings(s => s with { NetworkDomain = "arbib.pc" });
    await _o.StartAsync(ServiceId.Apache, default);
    _lan = [Lan("Wi-Fi", "192.168.1.40")];
    await _o.RefreshNetworkAsync(default);
    Assert.Equal([Ip("192.168.1.40")], _names.LastCall.Addresses);
    Assert.Equal(["192.168.1.40"], _o.Status().NameServiceAddresses);
}

[Fact]
public async Task Bind_failures_become_findings()
{
    _names.FailOn = Ip("192.168.100.254");
    Settings(s => s with { NetworkDomain = "arbib.pc" });
    var started = await _o.StartAsync(ServiceId.Apache, default);
    Assert.True(started.Ok);   // a port-53 clash never stops the web server
    Assert.Contains(_o.Status().Findings, f => f.Id == "dns-bind-192.168.100.254");
}

[Fact]
public async Task Saving_a_network_site_asks_for_the_firewall_when_rules_are_missing()
{
    _firewall = [];
    var r = await _o.SaveSiteAsync(NewSite("shop.test", AccessLevel.Network), default);
    Assert.Equal(_o.FirewallRequest(), r.RequiredFirewall);
    Assert.NotNull(r.RequiredFirewall);
}

[Fact]
public async Task Saving_when_the_rules_are_in_place_asks_nothing()
{
    _firewall = ExactRulesFor(_o.FirewallRequest() ?? new FirewallSetRequest(ActiveApache, [80, 443], false));
    Assert.Null((await _o.SaveSiteAsync(NewSite("shop.test", AccessLevel.Network), default)).RequiredFirewall);
}

[Fact]
public async Task Switching_apache_shows_the_firewall_finding_without_asking()
{
    _firewall = ExactRulesFor(new FirewallSetRequest(ActiveApache, [80, 443], false));
    Settings(s => s with { Network = new(true) });
    var r = await _o.ActivateAsync(ComponentKind.Apache, OtherApache, default);
    Assert.Null(r.RequiredFirewall);
    Assert.Contains(_o.Status().Findings, f => f.Id == NetworkFindings.FirewallId);
}
```

- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
  - The network refresh replaces the old production-findings slot in `Locked`. It is a private `RefreshNetworkCore(ct)` that:
    - computes `FirewallRequest()` (`FirewallRulePlanner.Needed` over current settings and sites);
    - computes `firewallReady` (`Satisfied` against `firewallRules()`; true when nothing is needed);
    - computes public addresses (LAN addresses whose `AdapterId` is in `publicAdapters()`);
    - keeps the last bind failures;
    - stores `NetworkFindings.For(...)`.
  - The name service is applied as `names.ApplyAsync(ApacheRunning ? _settings.NetworkDomain : null, lanAddresses().Select(a => a.Address))`, at the end of:
    - web start and stop;
    - `SaveSettingsAsync`;
    - `ActivateAsync(Apache)`;
    - `InitializeAsync` (after an auto start);
    - `RefreshNetworkAsync`.
  - `SaveSiteAsync`, `SetSiteEnabledAsync` and `SaveSettingsAsync` set `RequiredFirewall = FirewallRequest()` when that is not null and not satisfied. `ActivateAsync` never sets it.
  - `RefreshNetworkAsync` takes the gate (`Locked`), so it is serialized with every other operation, then raises `StatusChanged`.
- [ ] **Step 4: Run the Core and App suites.** Expected: PASS.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat: the Agent runs the name service and reports network problems"`

### Task 13: Window — network sharing card, copy buttons, banners

**Files:**
- Create: `src/Anvil.App/Services/{ITextClipboard,AvaloniaTextClipboard}.cs`
- Modify: `src/Anvil.App/ViewModels/{SettingsViewModel,SitesViewModel,DashboardViewModel}.cs`, `src/Anvil.App/Views/{SettingsView,SitesView,DashboardView}.axaml`, `src/Anvil.App/App.axaml.cs` (wiring), `src/Anvil.App/Services/EditorLauncher.cs` (only if `OpenUrl` refuses `ms-settings:`), `tests/Anvil.App.Tests/Support/AppFakes.cs`
- Test: `tests/Anvil.App.Tests/{SettingsViewModelTests,SitesViewModelTests,DashboardViewModelTests,PageViewTests}.cs`

**Interfaces:**
- Consumes: `ColleagueSetup`, `NetworkNames`, `LanAddresses` (Tasks 7–8), `IAgentApi.GetFirewallRequestAsync` / `RefreshNetworkAsync`, `AgentStatus.NameServiceAddresses`, `NetworkFindings` ids (Task 12)
- Produces:
  - `interface ITextClipboard { Task SetTextAsync(string text); }`
  - `SettingsViewModel`:
    - properties `NetworkDomain` (string), `ThisPcAddress` (string?), `WindowsCommand` (string?), `MacCommand` (string?), `NameServiceStatus` (string)
    - commands `SaveNetworkDomainCommand`, `CopyWindowsCommandCommand`, `CopyMacCommandCommand`
    - constructor gains `ITextClipboard clipboard, Func<IReadOnlyList<LanAddress>> lanAddresses`
  - `SiteRow.NetworkUrls`, `SiteRow.NetworkNote`, `HtdocsRow.NetworkUrls`; `SitesViewModel.CopyUrlCommand(string url)`
  - `FindingItem(string Id, string Message, string? Fix, bool Blocking)` with `ActionLabel` ("Open firewall" for `firewall-missing`, "Open network settings" for `network-public`, else null); `DashboardViewModel.RunFindingActionCommand(FindingItem f)`

- [ ] **Step 1: Write the failing tests.**

```csharp
// SettingsViewModelTests
[Fact]
public async Task Shows_the_colleague_commands_for_the_domain_and_address()
{
    _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc" };
    _lan = [Lan("Ethernet", "192.168.100.254")];
    await _vm.LoadAsync();
    Assert.Equal("192.168.100.254", _vm.ThisPcAddress);
    Assert.Equal("Add-DnsClientNrptRule -Namespace \".arbib.pc\" -NameServers 192.168.100.254", _vm.WindowsCommand);
    Assert.Equal("sudo mkdir -p /etc/resolver && echo \"nameserver 192.168.100.254\" | sudo tee /etc/resolver/arbib.pc", _vm.MacCommand);
}

[Fact]
public async Task Copy_puts_the_windows_command_on_the_clipboard()
{
    _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc" };
    await _vm.LoadAsync();
    await _vm.CopyWindowsCommandCommand.ExecuteAsync(null);
    Assert.Equal(_vm.WindowsCommand, _clipboard.Text);
}

[Fact]
public async Task No_domain_or_no_address_hides_the_commands()
{
    await _vm.LoadAsync();
    Assert.Null(_vm.WindowsCommand);
    _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc" };
    _lan = [];
    await _vm.LoadAsync();
    Assert.Null(_vm.WindowsCommand);
    Assert.Equal("This PC has no address on a local network.", _vm.NameServiceStatus);
}

[Fact]
public async Task Commands_follow_the_current_address()
{
    _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc" };
    await _vm.LoadAsync();
    _lan = [Lan("Wi-Fi", "192.168.1.40")];
    _api.RaiseStatus(Status(nameService: ["192.168.1.40"]));
    Assert.EndsWith("-NameServers 192.168.1.40", _vm.WindowsCommand);
}

[Theory]
[InlineData(null, false, new string[0], "Off: set a network domain to turn it on.")]
[InlineData("arbib.pc", false, new string[0], "Off while Apache is stopped.")]
[InlineData("arbib.pc", true, new[] { "192.168.100.254" }, "Answering for *.arbib.pc on 192.168.100.254.")]
public async Task Name_service_status(string? domain, bool apacheRunning, string[] listening, string text)
{
    _api.Settings = _api.Settings with { NetworkDomain = domain };
    await _vm.LoadAsync();
    _api.RaiseStatus(Status(apacheRunning, listening));
    Assert.Equal(text, _vm.NameServiceStatus);
}

[Fact]
public async Task Invalid_domain_is_explained_and_not_saved()
{
    await _vm.LoadAsync();
    _vm.NetworkDomain = "pc";
    await _vm.SaveNetworkDomainCommand.ExecuteAsync(null);
    Assert.Equal("A network domain needs at least two parts, such as arbib.pc.", _vm.Message);
    Assert.Empty(_api.SavedSettings);
}

// SitesViewModelTests
[Fact]
public async Task Rows_show_network_addresses()
{
    _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc", Network = new(true) };
    _api.Sites = [Site("shop.test", AccessLevel.Network)];
    _api.Htdocs = [Entry("blog", AccessLevel.Network)];
    _lan = [Lan("Ethernet", "192.168.100.254")];
    await _vm.LoadAsync();
    Assert.Equal(["http://shop.arbib.pc"], _vm.Sites.Single().NetworkUrls);
    Assert.Equal(["http://192.168.100.254/blog", "http://blog.arbib.pc"], _vm.Htdocs.Single().NetworkUrls);
}

// DashboardViewModelTests
[Fact]
public async Task Firewall_banner_button_runs_the_agents_request_then_refreshes()
{
    _api.FirewallRequest = new FirewallSetRequest("2.4.69", [80, 443], true);
    _api.RaiseStatus(StatusWith(new PreflightFinding("firewall-missing", Severity.Warning, "m", "f")));
    var item = _vm.Findings.Single();
    Assert.Equal("Open firewall", item.ActionLabel);
    await _vm.RunFindingActionCommand.ExecuteAsync(item);
    Assert.Equal(_api.FirewallRequest, Assert.Single(_elevator.Requests));
    Assert.Equal(1, _api.RefreshNetworkCalls);
}

[Fact]
public async Task Public_network_banner_opens_windows_settings()
{
    _api.RaiseStatus(StatusWith(new PreflightFinding("network-public", Severity.Warning, "m", "f")));
    await _vm.RunFindingActionCommand.ExecuteAsync(_vm.Findings.Single());
    Assert.Equal("ms-settings:network-status", _opened.Single());
}
```

- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
  - The Settings **Network sharing** card holds: the htdocs switch (moved here), the Network domain box with the placeholder `<computer name>.internal`, the name-service status, this PC's address, the Windows command with a copy button and the line "Your colleague runs this once in an administrator PowerShell window.", the Mac command with a copy button, and the note "If this PC's address changes, they run it again with the new one. A DHCP reservation in your router keeps the address fixed."
  - The card also lists the current `network-public` and `dns-bind-*` findings as text, so a partial port-53 clash and a Public network show on Settings as well as on the Dashboard.
  - Saving the domain normalizes it, shows `DomainError` when there is one, and otherwise saves and runs the result's `RequiredFirewall` through `ElevationFollowUp`.
  - Status text when the domain is set, Apache runs and nothing listens: "This PC has no address on a local network." if `lanAddresses()` is empty, else "Off: port 53 is in use on every network address."
  - The Sites page shows each row's network URLs with copy buttons, and its issue text in `NetworkNote`.
  - The Dashboard renders `ActionLabel` buttons on findings. The Public action calls `EditorLauncher.OpenUrl("ms-settings:network-status")` through a seam the test can observe.
  - `AvaloniaTextClipboard` uses the main window's `TopLevel` clipboard (Avalonia 12 API).
  - Extend `PageViewTests` so the Settings and Sites views bind the new properties headlessly.
- [ ] **Step 4: Run App tests.** Expected: PASS.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat: network sharing card with the colleague command and copy buttons"`

### Task 14: M1 — a failed config write never leaves a half-applied change

**Files:**
- Modify: `src/Anvil.Agent/Orchestrator.cs` (`Guard`, `ApplySitesAsync`, `ApplyConfigAsync`), `src/Anvil.Core/Apache/ConfigWriter.cs` if needed
- Test: `tests/Anvil.Core.Tests/Agent/SiteOperationsTests.cs`

- [ ] **Step 1: Write the failing test.**

```csharp
[Fact]
public async Task Locked_conf_folder_rolls_the_site_back()
{
    await StartWithOneSiteAsync();
    using var lockHandle = new FileStream(Path.Combine(_paths.Vhosts, "00-catchall.conf"), FileMode.Open, FileAccess.Read, FileShare.None);
    var r = await _o.SaveSiteAsync(NewSite("new.test", AccessLevel.Network), default);
    Assert.False(r.Ok);
    Assert.StartsWith("Anvil could not write the Apache configuration", r.Error);
    Assert.False(File.Exists(Path.Combine(_paths.Sites, "new.test.json")));
}
```

- [ ] **Step 2: Run it.** Expected: FAIL (an `IOException` escapes, or the site file stays).
- [ ] **Step 3: Implement.** Catch `IOException` and `UnauthorizedAccessException` from the writer around every apply. Convert them to a failed result with `Anvil could not write the Apache configuration ({message}). Nothing was changed.`, and run the undo list before returning.
- [ ] **Step 4: Run Core tests.** Expected: PASS.
- [ ] **Step 5: Commit.** `git commit -am "fix: a locked config folder no longer leaves a site half saved"`

### Task 15: M2 — installs never delete a running version and use the Agent's own catalog

**Files:**
- Modify: `src/Anvil.Core/Components/ComponentInstaller.cs`, `src/Anvil.Agent/Orchestrator.cs` (`InstallAsync`)
- Test: `tests/Anvil.Core.Tests/Components/ComponentInstallerTests.cs`, `tests/Anvil.Core.Tests/Agent/OrchestratorTests.cs`

**Interfaces:**
- Produces: `static bool ComponentInstaller.IsSafeVersion(string v)` (`^[0-9A-Za-z][0-9A-Za-z.\-]{0,31}$` and no `..`)

- [ ] **Step 1: Write the failing tests.**

```csharp
[Theory] [InlineData("2.4.69", true)] [InlineData("2026-10-20", true)] [InlineData(@"..\x", false)] [InlineData("a/b", false)] [InlineData("", false)]
public void Version_must_be_safe(string v, bool ok) => Assert.Equal(ok, ComponentInstaller.IsSafeVersion(v));

[Fact]
public async Task Reinstalling_an_installed_version_keeps_it()
{
    var first = await _installer.InstallAsync(Release("2.4.69"), null, default);
    var marker = Path.Combine(first.Path, "keep.txt"); File.WriteAllText(marker, "x");
    await _installer.InstallAsync(Release("2.4.69"), null, default);
    Assert.True(File.Exists(marker));
}

[Fact]
public async Task Install_uses_the_catalogs_url_and_hash_not_the_callers()
{
    var tampered = CatalogRelease("8.5.12") with { Url = "https://evil.example/php.zip", Sha256 = new string('0', 64) };
    await _o.InstallAsync(tampered, default);
    Assert.Equal(CatalogRelease("8.5.12").Url, _downloads.Single());
}

[Fact]
public async Task Release_missing_from_the_catalog_is_refused() =>
    Assert.Equal("Php 9.9.9 is not in Anvil's catalog.",
        (await Assert.ThrowsAsync<InvalidOperationException>(() => _o.InstallAsync(CatalogRelease("8.5.12") with { Version = "9.9.9" }, default))).Message);
```

- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
  - `InstallAsync` throws `ArgumentException` for an unsafe version. When the target is already a complete install listed by `Installed`, it returns that entry without downloading or deleting.
  - `Orchestrator.InstallAsync` replaces the caller's release with the catalog entry that has the same kind, version and `AppliesTo`.
- [ ] **Step 4: Run Core tests.** Expected: PASS.
- [ ] **Step 5: Commit.** `git commit -am "fix: installs keep running versions and trust only the catalog"`

### Task 16: M3 — self-update moves only release files

**Files:**
- Modify: `src/Anvil.Core/Update/Updater.cs` (`Swap`), `build/publish.ps1` (write `release-files.txt` into the app folder)
- Test: `tests/Anvil.Core.Tests/Update/UpdaterTests.cs`, `tests/Anvil.Core.Tests/Packaging/PublishLayoutTests.cs`

**Interfaces:**
- Produces: `const string Updater.FileList = "release-files.txt"`: one top-level name per line, written by `publish.ps1`, itself included in the list.

- [ ] **Step 1: Write the failing test.**

```csharp
[Fact]
public void User_file_at_the_root_survives_two_updates()
{
    var target = InstalledCopy("1.0.0", files: ["Anvil.exe", "Anvil.Core.dll"]);   // writes release-files.txt
    File.WriteAllText(Path.Combine(target, "my-notes.txt"), "mine");
    Updater.Swap(Staged("1.0.1"), target, pid: 0, TimeSpan.Zero);
    Updater.Swap(Staged("1.0.2"), target, pid: 0, TimeSpan.Zero);
    Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "my-notes.txt")));
}

[Fact]
public void Copy_without_a_file_list_moves_only_names_the_new_release_brings()
{
    var target = InstalledCopy("1.0.0", files: ["Anvil.exe"], withList: false);
    File.WriteAllText(Path.Combine(target, "my-notes.txt"), "mine");
    Updater.Swap(Staged("1.0.1"), target, pid: 0, TimeSpan.Zero);
    Assert.True(File.Exists(Path.Combine(target, "my-notes.txt")));
}
```

- [ ] **Step 2: Run them.** Expected: FAIL (the first one).
- [ ] **Step 3: Implement.** `Swap` moves into `.update\previous-<v>` only the names in the installed copy's `release-files.txt`. Without the file, it moves the names that also exist at the staged release's top level. `runtime\`, `data\`, `htdocs\` and `.update\` are never moved, whatever a list says. Have `publish.ps1` write the list; add a layout test asserting it exists and names `Anvil.exe`.
- [ ] **Step 4: Run Core tests.** Expected: PASS.
- [ ] **Step 5: Commit.** `git commit -am "fix: self-update leaves your own files in the Anvil folder alone"`

### Task 17: M4 — the hosts file is replaced atomically

**Files:**
- Modify: `src/Anvil.Elevator/Handlers/HostsHandler.cs`, `src/Anvil.Core/IO/AtomicFile.cs` (add a method if none fits)
- Test: `tests/Anvil.Core.Tests/IO/AtomicFileTests.cs`

**Interfaces:**
- Produces: `static void AtomicFile.ReplaceText(string path, string text, Encoding encoding)`. It writes `<path>.anvil-tmp` in the same folder, then `File.Replace(tmp, path, null)` when the file exists (which keeps its ACL and attributes), else `File.Move`.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact]
public void Replaces_content_and_leaves_no_temp_file()
{
    var f = Path.Combine(_dir, "hosts"); File.WriteAllText(f, "old");
    AtomicFile.ReplaceText(f, "new", Encoding.Latin1);
    Assert.Equal("new", File.ReadAllText(f, Encoding.Latin1));
    Assert.Single(Directory.GetFiles(_dir));
}

[Fact]
public void Keeps_the_files_attributes()
{
    var f = Path.Combine(_dir, "hosts"); File.WriteAllText(f, "old");
    File.SetAttributes(f, FileAttributes.Archive | FileAttributes.Hidden);
    AtomicFile.ReplaceText(f, "new", Encoding.Latin1);
    Assert.True(File.GetAttributes(f).HasFlag(FileAttributes.Hidden));
}
```

- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement** `ReplaceText`, and use it in `HostsHandler` in place of `File.WriteAllText` (keep Latin-1).
- [ ] **Step 4: Run Core tests.** Expected: PASS.
- [ ] **Step 5: Commit.** `git commit -am "fix: a crash while saving can no longer empty the hosts file"`

### Task 18: M6 — values reaching Apache directives are checked

**Files:**
- Modify: `src/Anvil.Core/Sites/SiteValidator.cs`, `src/Anvil.Core/Apache/ConfigRenderer.cs`
- Test: `tests/Anvil.Core.Tests/Sites/SiteValidatorTests.cs`, `tests/Anvil.Core.Tests/Apache/ConfigRendererTests.cs`

**Interfaces:**
- Produces: `static bool SiteValidator.IsDirectiveSafe(string value)`: false for `"`, any control character (CR and LF included), `%{` or `${`.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Theory]
[InlineData("C:\\a\"b")] [InlineData("C:\\a\r\nInclude x")] [InlineData("C:\\%{ENV:x}")] [InlineData("C:\\${SRVROOT}")]
public void Root_with_directive_characters_is_refused(string root) =>
    Assert.Contains(SiteValidator.Validate(Site() with { Root = root }, []), e => e.Field == "Root"
        && e.Message == "The folder cannot contain quotes, line breaks, %{ or ${.");

[Fact]
public void Index_and_proxy_url_are_checked_too()
{
    Assert.Contains(SiteValidator.Validate(Site() with { Index = ["index.php\"x"] }, []), e => e.Field == "Index");
    Assert.Contains(SiteValidator.Validate(Site() with { Type = SiteType.Proxy, Root = "http://127.0.0.1:5173/${x}" }, []), e => e.Field == "Root");
}

[Fact]
public void Renderer_refuses_an_unchecked_value() =>
    Assert.Throws<ArgumentException>(() => Render(Input(Site() with { Root = "C:\\a\nb" })));
```

- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** The validator checks `Root`, each `Index` entry, and each htdocs alias root it validates. The renderer calls `IsDirectiveSafe` on every site- or entry-supplied string it writes into a directive, and throws `ArgumentException` naming the field.
- [ ] **Step 4: Run Core tests.** Expected: PASS.
- [ ] **Step 5: Commit.** `git commit -am "fix: refuse quotes, line breaks and variables in site paths"`

### Task 19: M8 — a PHP update must answer a request before it is kept

**Files:**
- Create: `src/Anvil.Core/Php/FastCgiProbe.cs`
- Modify: `src/Anvil.Agent/Orchestrator.Updates.cs` (`UpdatePhpAsync`), `src/Anvil.Agent/Orchestrator.cs` (optional constructor parameter `Func<int, CancellationToken, Task<bool>>? phpProbe = null`; null means always true)
- Test: `tests/Anvil.Core.Tests/Php/FastCgiProbeTests.cs` (fake TCP FastCGI peer), `tests/Anvil.Core.Tests/Agent/OrchestratorTests.cs`, `tests/Anvil.Integration.Tests/PhpPoolTests.cs`

**Interfaces:**
- Produces: `static Task<bool> FastCgiProbe.ProbeAsync(int port, string scriptPath, TimeSpan timeout, CancellationToken ct)`. It sends one FastCGI `BEGIN_REQUEST` (role RESPONDER) with `SCRIPT_FILENAME`, `REQUEST_METHOD=GET` and empty `STDIN`. It returns true when `STDOUT` contains `anvil-probe-ok` before `END_REQUEST`. The probe script `data\run\probe.php` (`<?php echo 'anvil-probe-ok';`) is written by the Agent before the probe.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact]
public async Task Probe_reads_the_scripts_answer()   // FastCgiProbeTests, with a TcpListener that answers one FastCGI request
{
    var peer = FakeFastCgiPeer.Answering("Content-type: text/html\r\n\r\nanvil-probe-ok");
    Assert.True(await FastCgiProbe.ProbeAsync(peer.Port, @"C:\x\probe.php", TimeSpan.FromSeconds(2), default));
}

[Fact]
public async Task Peer_that_closes_fails_the_probe()
{
    var peer = FakeFastCgiPeer.Closing();
    Assert.False(await FastCgiProbe.ProbeAsync(peer.Port, @"C:\x\probe.php", TimeSpan.FromSeconds(2), default));
}

// OrchestratorTests
[Fact]
public async Task Php_update_that_does_not_answer_goes_back()
{
    _phpProbe = (_, _) => Task.FromResult(false);
    var r = await _o.ActivateAsync(ComponentKind.Php, "8.5.13", default);
    Assert.False(r.Ok);
    Assert.Equal("PHP 8.5.13 started but did not answer a request, so Anvil went back to PHP 8.5.12.", r.Error);
    Assert.Equal("8.5.12", _o.GetPhp().Single(p => p.IsDefault).Version);
}

// PhpPoolTests (integration, real php-cgi)
[Fact]
public async Task Real_pool_answers_the_probe() => Assert.True(await FastCgiProbe.ProbeAsync(_pool.Port, _probeScript, TimeSpan.FromSeconds(5), default));
```

- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** Write a FastCGI client per the FastCGI 1.0 record layout: an 8-byte header (version 1, type, request id 1, content length, padding), name-value pairs with 1- or 4-byte lengths, and reads that loop until `END_REQUEST` or the timeout. In `UpdatePhpAsync`, after the new pool starts and before committing, write the probe script, call the probe, and on false use the existing rollback path with the message above.
- [ ] **Step 4: Run Core tests, then** `dotnet test --project tests/Anvil.Integration.Tests -- --filter-class "*PhpPoolTests"`. Expected: PASS.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "fix: roll back a PHP update that cannot answer a request"`

### Task 20: M9 — confine what the window writes under data\

**Files:**
- Modify: whatever the window writes under `data\` (find with `grep -rn "Logs\|Tmp\|File.Write\|Directory.Create" src/Anvil.App`), `src/Anvil.Core/Elevation/{RequestValidator,UacElevator}.cs`, `src/Anvil.Core/Logging/Logging.cs`, `docs/superpowers/specs/2026-10-27-arbipache4-design.md` (Architecture: the "only writer" sentence)
- Test: `tests/Anvil.Core.Tests/Elevation/RequestValidatorTests.cs`

**Decision (made here):** keep the split and reword the rule. The Agent is the only writer of `data\` except `data\logs\app\` (the window's own log) and `data\tmp\elevation\` (requests the window hands to the Elevator). Moving those writes into the Agent would put the Elevator handshake behind the pipe for no gain.

- [ ] **Step 1: Write the failing test.**

```csharp
[Fact]
public void Request_outside_the_elevation_folder_is_refused()
{
    var stray = Path.Combine(_paths.Tmp, "r.json");
    Assert.Equal("Requests must sit in data\\tmp\\elevation.", RequestValidator.PathError(stray, _paths));
    Assert.Null(RequestValidator.PathError(Path.Combine(_paths.Tmp, "elevation", "r.json"), _paths));
}
```

- [ ] **Step 2: Run it.** Expected: FAIL.
- [ ] **Step 3: Implement.** Point `UacElevator` at `data\tmp\elevation\` and the window's Serilog file at `data\logs\app\`. Make sure no other window write lands under `data\` (fix any found). Update `PathError`, the log source discovery (`LogSources`), and the spec sentence.
- [ ] **Step 4: Run the Core and App suites.** Expected: PASS.
- [ ] **Step 5: Commit.** `git add -A && git commit -m "fix: the window writes only its log and Elevator requests under data"`

### Task 21: Licence, changelog and user documents

**Files:**
- Replace: `LICENSE` (GPL-3.0)
- Modify: `CHANGELOG.md`, `README.md`, `SECURITY.md`, `CONTRIBUTING.md`, `packaging/README.txt`, `docs/superpowers/specs/2026-10-27-arbipache4-design.md` (pointer line at the top; M7: "10 s" → "15 s" where it describes the reload wait), and licence metadata in project files if any (`grep -rn "Apache-2.0\|PackageLicense" .`)

- [ ] **Step 1: Fetch the licence.** `curl -L https://www.gnu.org/licenses/gpl-3.0.txt -o LICENSE`. Check: the first line is `                    GNU GENERAL PUBLIC LICENSE`, `Version 3, 29 June 2007` is present, and `wc -l LICENSE` prints `674`.
- [ ] **Step 2: Check package licences.** List shipped packages (`dotnet list Anvil.sln package --include-transitive`) and confirm each is MIT, BSD, Apache-2.0 or OFL (IBM Plex fonts). Report any other licence instead of shipping it.
- [ ] **Step 3: Write `CHANGELOG.md`.** Replace both 4.x entries with one `## 1.0.0 — unreleased` entry for users:
  - Start with one line: "Anvil is the new name of ArbiPache 4: a rewrite of ArbiPache 3 for development on your own PC."
  - **Added:** everything from the old 4.0.0 Added list that still exists, plus network sharing: This PC / My network per site, network names under your own domain answered by Anvil, the colleague command with a copy button, warnings for Public networks and blocked firewall rules.
  - **Changed:** C# / Avalonia, everything in one folder, no PyInstaller, certificates made by Anvil, development settings written once into `php.ini` / `my.ini`, Xdebug as a normal extension.
  - **Removed:** the command line, pvm.
  - Never mention features that never shipped (production mode, Let's Encrypt, service, IP sites).
- [ ] **Step 4: Rewrite the documents.**
  - `README.md` explains Anvil for development:
    - installing and first run;
    - the three ways to open a site: `localhost/<folder>`, `.test` domains, and for others `http://<IP>/<folder>` or `<app>.<your domain>`;
    - the colleague command, and how to undo it with `ColleagueSetup.WindowsUndoCommand` text;
    - switching a Public network to Private;
    - licence GPL-3.0 with the badge;
    - a link to ArbiPache 3 (`https://github.com/ArbibSoufiane/ArbiPache/tree/v4.1.0`).
  - `CONTRIBUTING.md`: contributions under GPL-3.0.
  - `SECURITY.md`: the local-network-only scope.
  - `packaging/README.txt`: the same story in plain text; remove the service and production sections.
- [ ] **Step 5: Check.** `grep -rn -i "production\|let's encrypt\|service\|arbipache" README.md CHANGELOG.md SECURITY.md CONTRIBUTING.md packaging/README.txt` shows only intended mentions (ArbiPache 3 history, "web server" uses). Run `dotnet test --project tests/Anvil.Core.Tests` (packaging tests read these files). Expected: PASS.
- [ ] **Step 6: Commit.** `git add -A && git commit -m "docs: GPL-3.0 licence, Anvil 1.0.0 changelog and documents"`

### Task 22: Code signing guide

**Files:**
- Create: `docs/code-signing.md`

- [ ] **Step 1: Check the source.** Read SignPath Foundation's current terms and onboarding pages (signpath.org) with WebFetch. Note their eligibility rules (OSI licence, public repository, CI builds, no proprietary parts), the roles and multi-factor requirement, the code-signing-policy text the project must publish, and how artifacts reach them (GitHub Actions connector, artifact configuration).
- [ ] **Step 2: Write the guide.** Sections:
  1. Why sign (SmartScreen, Defender).
  2. Eligibility, with how Anvil meets each rule: GPL-3.0, public repo `ArbibSoufiane/Anvil`, builds by `release.yml`.
  3. Before applying: create the repo, push, make a test release with `build/publish.ps1`.
  4. Applying, step by step.
  5. The code-signing policy and privacy statement to add to `README.md`. The privacy statement says Anvil contacts the network only for update checks and component downloads the user starts.
  6. The `release.yml` steps that would upload `dist\Anvil` to SignPath and download the signed files, as text with a "not enabled" note.
  7. Pinning the certificate subject in the updater (`Updater` `requiredPublisher`) once the first signed release exists.
  8. Checking a signed build: `Get-AuthenticodeSignature`, `signtool verify /pa /v`.
  9. Scanning with Defender (`MpCmdRun -Scan -ScanType 3 -File <dir>`) and VirusTotal before publishing.

  Cite the SignPath pages used, with the date read.
- [ ] **Step 3: Commit.** `git add docs/code-signing.md && git commit -m "docs: how to sign Anvil releases with SignPath Foundation"`

### Task 23: Manual test checklist

**Files:**
- Create: `docs/manual-test-checklist.html`

- [ ] **Step 1: Load the design skills.** Invoke `artifact-design` (and `artifact-capabilities` for the published copy's tick storage) before writing.
- [ ] **Step 2: Write the page.** One self-contained HTML file: no network, inline CSS and JS, light and dark themes, readable on a phone. Each item has a checkbox, a short "how" and an "expect" line, plus a notes box. Ticks and notes are kept in `localStorage` (wrapped in try/catch), with a progress count, a Reset button and print styles. Sections:
  1. **Clean up ArbiPache 4 leftovers:** the `# >>> ArbiPache >>>` hosts block, firewall rules named `ArbiPache HTTP *`, the trusted ArbiPache CA in `certmgr.msc` → Trusted Root, the Run key `HKCU\…\Run\ArbiPache`.
  2. **First run:** unzip, open, window, tray, open twice brings it to the front.
  3. **Apache:** install, start, `http://localhost/`, threads, modules, open `httpd.conf`.
  4. **PHP:** install two versions, default, pool workers, extensions, Xdebug on and off, `php.ini` edit wins after restart.
  5. **Database:** install, root password, settings page, backup.
  6. **Sites:** add a `.test` site, the hosts prompt, local HTTPS trust prompt and padlock, proxy to Vite, a Laravel project detected.
  7. **Network sharing on this PC:** set `arbib.pc`; the Public-network warning and its button, switched to Private; the Open firewall prompt; no Windows "Allow access" prompt afterwards; the name-service status.
  8. **From a colleague's PC:** run the copied command; open `http://<app>.arbib.pc` and `http://<IP>/<folder>`; add a new app and open it at once; a This PC site is refused; HTTPS on a network name is refused; undo with the remove command.
  9. **Port 53 busy:** with ICS on, the finding shows, and IP/folder still works.
  10. **Address change:** switch networks; the status and command follow.
  11. **Updates:** components update and roll back; app self-update end to end after the first GitHub release; a user file at the app root survives.
  12. **Start with Windows** and close to tray.
  13. **Release checks:** Defender scan, VirusTotal, signature (after SignPath).
- [ ] **Step 3: Publish it.** Publish the page as a private artifact for the owner (the `Artifact` tool, icon `checklist`). Keep the repo file as the source.
- [ ] **Step 4: Commit.** `git add docs/manual-test-checklist.html && git commit -m "docs: manual test checklist"`

### Task 24: Final verification

- [ ] **Step 1:** `dotnet build Anvil.sln -c Release` gives 0 warnings and 0 errors.
- [ ] **Step 2:** Run all three suites. Expected: all pass; record the counts.
- [ ] **Step 3:** Run `pwsh build/publish.ps1`. Expected: it produces `dist\Anvil\` (with `release-files.txt`), `dist\Anvil-1.0.0-win64.zip` and `dist\SHA256SUMS`.
- [ ] **Step 4:** `grep -rn -i "arbipache" src tests build .github catalog` shows only allowed lines (`NamingTests` also covers `src`).
- [ ] **Step 5:** Start `dist\Anvil\Anvil.exe` once. The window opens, the Agent starts, and the Settings card shows the address. Take a screenshot for the report.
- [ ] **Step 6:** Update the rulings: list every call made while executing this plan in the final report.
