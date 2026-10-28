# Anvil — ArbiPache made development only, and renamed

Date: 2026-10-28 · Status: approved by the owner · Replaces the production parts of
[2026-10-27-arbipache4-design.md](2026-10-27-arbipache4-design.md); everything that document says and
this one does not change still holds.

## Intent

ArbiPache 4 was built for two jobs: development on the owner's PC and production on client servers
facing the internet. Most of the open issues from the final review (the rulings log,
https://claude.ai/artifact/31yu39B1pwxcGbNHcgieNP) sit in the production half: the Windows service,
folder permissions for the service, PHP running under the service account, internet access rules,
Let's Encrypt. The owner has decided Anvil is for development only. Removing the production half
makes the program smaller, removes those risks instead of patching them, and leaves one job done well.

One development need is new: sometimes the owner wants someone else on the same network to open
their sites. They want two ways to hand out an address:

- `http://<my IP>/<folder>`, which already works for htdocs folders, and
- `http://<app>.arbib.pc`: the colleague does a one-time setup on their machine, and from then on every
  app the owner has open to the network answers at `<app name>.arbib.pc`, with nothing more to do when
  a new app is added.

The second one cannot be done with the hosts file (Windows hosts files have no wildcards), so
Anvil answers name lookups for that domain itself, and the colleague points that one domain at the
owner's PC with a single command. The owner asked for that command to sit in the window with a copy
button.

The product is renamed **Anvil**. The owner found "ArbiPache" ugly. The new name also drops "Apache",
a trademark of the Apache Software Foundation, from the product name. Anvil gets its own GitHub
repository, `github.com/ArbibSoufiane/Anvil`, which the owner creates. ArbiPache 3 stays untouched in
its repository, so its updater never offers its users a different program. With a fresh repository
there is no clash with ArbiPache 3's v4.1.0, and Anvil starts at **1.0.0**.

The licence becomes **GPL-3.0**: the owner wants
people to use the program, read the source and suggest changes, and does not want others to sell it
as a closed product. GPL-3.0 still allows selling, but whoever sells must publish the full source
under GPL-3.0, and buyers may pass it on for free. It also keeps the project eligible for free code
signing through SignPath Foundation, which only signs OSI-licensed projects. A non-commercial licence
was ruled out for that reason.

## Goals

1. Remove every production-only feature, its settings, its UI, its code, its tests and its packages.
2. Each domain site, and htdocs, is open to **This PC** or to **My network**. Nothing can be opened
   to the internet.
3. Network names: with a network domain set (the owner will use `arbib.pc`), every site and htdocs
   folder that is open to the network also answers at `<label>.<network domain>` for other devices.
4. Anvil answers DNS lookups for the network domain, on the local network only.
5. The window shows the exact command a colleague runs once, filled in with this PC's address and the
   network domain, with a copy button.
6. `http://<this PC's IP>/<folder>` keeps working for htdocs folders and aliases.
7. Development settings are fixed. They are written into the user's `php.ini` and `my.ini` once,
   when those files are created, so the user's later edits win.
8. The name Anvil, version 1.0.0, GPL-3.0, documentation updated to match.
9. A guide to signing releases through SignPath Foundation, for the owner to follow after testing.
10. A manual test checklist the owner can tick through in a browser.
11. Fix the eight deferred minors from the final review that are not about production (see
    "Review leftovers").

## Non-goals

- No migration of ArbiPache 4 data. 4.0.0 and 4.1.0 were never released. Data left over from the
  owner's own test runs must load without crashing (see Constraints), but it is not converted
  carefully.
- No HTTPS for network names. Colleagues do not trust this PC's local certificate authority.
- No setup for phones or tablets on network names. They use `http://<IP>/<folder>`.
- No forwarding or recursive DNS. Anvil never resolves names outside the network domain.
- No change to how secrets are stored (database password, local CA key). The service was one reason
  for the current choices; revisiting them is a separate change.
- No new CI wiring for signing. The guide includes example workflow steps; nothing is turned on.

## Anti-goals (failure even if it "works")

- Any site, htdocs folder or the name service reachable from the internet, or from a network Windows
  calls Public.
- The name service answering names outside the network domain, or answering askers outside private
  address ranges: that makes an open resolver usable for DNS amplification.
- A user's own `php.ini` or `my.ini` edit silently overridden by a file Anvil generates.
- Production leftovers visible in the window: a setting, a badge, a page or a message that refers to
  something that no longer exists.
- A port-53 clash (another program holding it) that stops Apache or the sites from starting.

## What goes

| Feature | Where it lives today (not exhaustive) |
|---|---|
| Development/Production switch, Mode page, mode switch dialog, editable profiles, mode backups | `Core/Modes/*`, `Agent/Orchestrator.Mode.cs`, `App/ViewModels/Mode*`, `App/Views/Mode*`, `ServerMode`, `XdebugInProduction` |
| Production warnings on the Dashboard | `Core/Preflight/ProductionChecks.cs`, Dashboard view model and view |
| Production catch-all (403 for unknown hosts) and production security headers | `00-catchall*.sbn`, `mode.conf.sbn` |
| Windows service: install, uninstall, run as service, service SID in pipe and file ACLs | `App/Services/ServiceSwitch.cs`, `WindowsAgentService.cs`, `Elevator/Handlers/ServiceHandler.cs`, `RunAsService`, `ControlUserSids`, `Microsoft.Extensions.Hosting.WindowsServices`, `System.ServiceProcess.ServiceController` |
| "Secure this folder" and the folder checks done for the service | `Core/Elevation/ServiceFolderCheck.cs`, `AclPlanner.cs`, `FolderPermissions.cs`, `Elevator/Handlers/AclHandler.cs`, `FolderHandler.cs` |
| Let's Encrypt, nightly renewal, DNS check | `Core/Certificates/AcmeService.cs`, `CertesAcmeProtocol.cs`, `IAcmeProtocol.cs`, `DnsCheck.cs`, `RenewalPlanner.cs`, `Agent/RenewalWorker.cs`, `AcmeEmail`, `AcmeStaging`, `Certes` |
| Importing a certificate a client sent (`.pfx`, `.crt` + `.key`) | `Core/Certificates/CertImporter.cs`, `CertificateImportException.cs`, its UI |
| HTTP behind a client's load balancer | `_lb.sbn`, `TrustedProxies`, `AcceptOnlyFromProxies`, `CertSource.LoadBalancer` |
| IP sites ("single site") and extra folders (`/demo`) | `SiteKind.Ip`, `SiteMount`, `Mounts`, `_mount.sbn`, IP handling in the renderer, validator and Add Site dialog |
| Access "Everyone" and "Specific IPs", and password protection | `AccessLevel.Everyone`, `AccessLevel.SpecificIps`, `AllowedIps`, `PasswordProtected`, `HtpasswdFile.cs`, `data\auth\`, `SiteSecrets` password parts, `BCrypt.Net-Next` |
| "Internet" firewall scope | `FirewallScope.Internet` |
| Network setting that could allow more than private ranges | `NetworkAccess.PrivateNetworksOnly` |
| ModSecurity and Core Rule Set leftovers | `ComponentKind.ModSecurity`, `ComponentKind.CoreRuleSet` |

The Elevator keeps only `hosts-set` and `firewall-set`. A removed verb is answered "Unknown request.",
as any unknown verb is today.

## What stays

- Domain sites (`shop.test` and similar) and htdocs folders, including aliases to folders outside
  htdocs. Site types PHP, static and proxy (Vite with websockets). Project detection.
- Local development HTTPS from Anvil's own certificate authority, with the HTTP-to-HTTPS redirect.
- Hosts file entries and firewall rules through the Elevator and UAC.
- Installing, updating, switching and rolling back Apache, PHP and MariaDB/MySQL.
- PHP worker pools, Apache thread tuning, optional Apache modules.
- Database settings page, root password change, backups.
- Logs page and log rotation.
- Self-update with `SHA256SUMS`, and the Authenticode check that applies once releases are signed.
- Start with Windows, close to tray, single instance, the ArbiPache 3 import.
- The Agent as a child process of the window. It is now the only way the Agent runs.

## Access model

- `AccessLevel` has two values: `ThisPc` (`Require local`) and `Network` (local plus the private
  ranges, as `ConfigRenderer.PrivateRanges` renders today).
- New domain sites default to **My network**. The firewall rule only opens on Private and Domain
  network profiles, so on a network Windows calls Public (café Wi-Fi) nothing is reachable from
  outside.
- htdocs keeps its global "open to my network" setting (default on), and an htdocs entry never opens
  wider than that setting, as today.
- **Firewall rules are per program and per port.** Anvil's rules allow:
  - the active Apache version's `httpd.exe` on TCP at the HTTP and HTTPS ports, and
  - `Anvil.Agent.exe` on UDP 53 when a network domain is set.

  Scope is always the local subnet on the Private and Domain profiles. Port-only rules are no longer
  made.
- **Why per program.** All profiles on the owner's PC have "notify on listen" on. Without a rule for
  the program, Windows shows its own "Allow access?" prompt for each new `httpd.exe` path and for the
  Agent. That prompt can create an Allow-on-Public rule. If the user clicks Cancel, it creates a Block
  rule, and a Block rule beats every Allow rule. To stop that:
  - `firewall-set` also removes any other rule that names one of Anvil's own program paths.
  - The Elevator accepts program paths only inside the Anvil folder:
    `runtime\apache\<version>\bin\httpd.exe` for an installed version, and the folder's own
    `Anvil.Agent.exe`.
  - The checklist confirms that Windows no longer prompts once the program rule exists.
- **When Anvil asks.** Reading the firewall rules needs no administrator rights, so the Agent (or
  the window) compares the rules that exist with the rules needed. Anvil never shows a UAC
  prompt on its own, at launch or otherwise.
  - Rules missing while something is open to the network: a banner on the Dashboard and the Network
    sharing card says other devices cannot reach this PC yet, with an **Open firewall** button.
    The button runs `firewall-set` through UAC, using the existing `ElevationFollowUp` flow.
  - The same banner appears after an Apache switch, because the new `httpd.exe` path needs its rule.
  - Saving a site or turning a network switch on may run the same request directly, as the htdocs
    switch does today.
  - Anvil never asks to close rules. A rule left behind with nothing open is harmless: Apache
    answers such requests with `Require local`.
- **Public networks.** Anvil reads the Windows network category of the adapters it serves on.
  This is only a check; the firewall does the blocking.
  - When an adapter is Public, the Dashboard and the Network sharing card say: "Windows treats this
    network as Public, so other devices are blocked."
  - The message comes with a button that opens Windows' network settings, where the user switches the
    network to Private. Anvil does not switch it itself.
  - The owner's own Ethernet is Public today, so the owner will see this message first.

## Network names

**Setting.** Settings gets a **Network domain** field, empty by default (feature off). The placeholder
suggests `<computer name>.internal` (`.internal` is reserved by ICANN for private use). The owner types
`arbib.pc`. The value must be a valid DNS name of at least two labels and must not be `localhost`,
end in `.localhost`, or be `test`.

**Which names exist.** With a network domain set:

- A domain site open to the network answers at `<first label of its name>.<network domain>`:
  `shop.test` → `shop.arbib.pc`. Aliases do not get network names.
- An htdocs folder or alias open to the network answers at `<folder>.<network domain>`:
  `htdocs\blog` → `blog.arbib.pc`, served from the same folder with the same PHP version as
  `http://<IP>/blog`.
- The bare network domain (`arbib.pc`) serves what `http://<IP>/` serves (the htdocs catch-all).
- A label must be a valid DNS label (lower case `a-z`, `0-9`, `-`, 1 to 63 characters, not starting or
  ending with `-`). A folder whose name cannot be one gets no network name, and the Sites page says
  why.
- Clashes: a domain site beats an htdocs folder; between domain sites, the one created first wins.
  The loser has no network name, and the Sites page names the winner.
- Network names are HTTP only and never redirect to HTTPS, even when the site itself redirects.

**Name service.** A small DNS responder in the Agent:

- **Which addresses it listens on.** UDP port 53 on each of this PC's LAN addresses separately, never
  on `0.0.0.0`.
  - A LAN address is a private IPv4 address on an adapter that is up and has a default gateway.
  - Virtual adapters are skipped: Hyper-V and WSL `vEthernet`, VMware, VirtualBox, and any adapter
    without a gateway.
  - Why separately: on the owner's PC, Windows Internet Connection Sharing (SharedAccess, turned on by
    Hyper-V and WSL) already holds UDP `0.0.0.0:53`. Binding one specific address still works next to
    it (tested).
  - Why skip virtual adapters: listening there would take over name lookups for the VMs behind them,
    and REFUSED for every outside name would break their internet access.
- **When it runs.** While the web server runs and a network domain is set. It starts and stops with
  Apache, and listens again whenever this PC's addresses change.
- **Who gets an answer.** Only askers whose address is in a private range. Everything else is dropped
  without a reply.
- **What it answers.**
  - An `A` query for the network domain or any name under it gets one answer: the address the query
    arrived on, with a TTL of 60 seconds.
  - Every name under the domain gets that answer, claimed or not. An unclaimed name reaches Apache's
    catch-all, and the colleague's PC never caches "does not exist" for a site the owner adds a
    minute later.
  - `AAAA` and other types get an empty `NOERROR` answer, so clients fall back to IPv4 at once.
  - Any other name gets `REFUSED`. It never forwards, never recurses, and never answers over TCP.
- **When an address cannot be bound.** The web server still starts. Settings and the Dashboard show
  "Network names are off on <address>: port 53 is in use there", and `http://<IP>/<folder>` keeps
  working.
- **Bad input.** A malformed packet is dropped and never stops the responder.

**Colleague setup, in the window.** Settings gets a **Network sharing** card with:

- the htdocs "open to my network" switch (moved here),
- the Network domain field,
- the name service status,
- this PC's address (`LanAddress`),
- the Windows command, filled in, with a copy button:
  `Add-DnsClientNrptRule -Namespace ".arbib.pc" -NameServers 192.168.1.5`
  with the line "Your colleague runs this once in an administrator PowerShell window.",
- the Mac command, filled in, with a copy button:
  `sudo mkdir -p /etc/resolver && echo "nameserver 192.168.1.5" | sudo tee /etc/resolver/arbib.pc`,
- a note: "If this PC's address changes, they run it again with the new one. A DHCP reservation in
  your router keeps the address fixed."

The Sites page shows each network-open site's network addresses (`http://shop.arbib.pc`, and for
htdocs folders also `http://192.168.1.5/blog`) with a copy button.

## Development settings

- `php.ini` is still created once from the version's `php.ini-development`. When it is created, the
  development values (today's `BuiltInProfiles.Development.Php`) are written into it directly.
  ArbiPache no longer generates `conf.d\99-arbipache-mode.ini`; on start it deletes that file if it
  still carries Anvil's generated marker. The seven managed path keys are kept up to date as
  today.
- Xdebug is an ordinary extension on the PHP page: on after install, and the user can turn it off. The
  "managed by mode" lock goes.
- `my.ini` gets the development values (today's development database profile) once, when it is
  created. Anvil stops rewriting them afterwards.
- Apache's development values stay in Anvil's generated configuration (`data\conf\` is fully
  regenerable and not the user's file). No security headers.
- The catch-all for unknown hosts is the htdocs listing for private ranges, as Development is today.

## The rename

Everything that carries the old name moves to Anvil:

- **Projects, namespaces and the solution:** `Anvil.Core`, `Anvil.Agent`, `Anvil.App`,
  `Anvil.Elevator`, the test projects `Anvil.*.Tests`, and `Anvil.sln`.
- **Executables:** `Anvil.exe`, `Anvil.Agent.exe`, `Anvil.Elevator.exe`.
- **Settings file:** `data\anvil.json`. The log folder follows the new name too.
- **Pipe and mutex names:** `Anvil.Agent`, `Local\Anvil.App`, `Anvil.App.<user SID>`, and the Agent's
  mutex.
- **Markers:** the firewall rule names, the hosts file block markers, the marked blocks in
  `httpd.conf`, `php.ini` and `my.ini`, and the "Generated by" lines.
- **Local certificate authority:** its name, for example "Anvil Local Development CA".
- **Start with Windows:** the Run key value name.
- **The editor choice** kept in the user profile.
- **The updater's release feed:** `ArbibSoufiane/Anvil`. Until the repository has a release, the update
  check says there is nothing to install.
- **The release zip name and the window title.**
- **`CLAUDE.md`:** its paths and names (`Anvil.Core.BuildInfo.Version`, the test project paths), and
  its versioning examples.

Some things keep the old name on purpose:

- "Import from ArbiPache 3" and the paths it reads (`%ARBIPACHE_HOME%`, `%USERPROFILE%\.arbipache`),
  because they refer to the old program.
- The local folder `D:\Projects\ProductionServerManager` and the branch `feat/arbipache4`.

**Leftovers on the owner's PC.** ArbiPache 4 test runs may have left a hosts block, firewall rules, a
trusted "ArbiPache" CA and a Run key on the owner's PC. Anvil does not clean them up (no migration);
the test checklist starts with removing them.

## Review leftovers

The owner chose to fix in 1.0.0 the eight deferred minors from the final review that are not about
production. Each gets a failing test first, as in the review pass. The rulings log has the details
(https://claude.ai/artifact/31yu39B1pwxcGbNHcgieNP).

- **M1. Config errors skip the undo.** `ConfigWriter`'s `Directory.Move` can throw an `IOException`
  (antivirus or the indexer locking `data\conf`). It escapes `SaveSiteAsync` before the undo runs, so
  the site file stays saved but its config is never applied. Every failure must roll back.
- **M2. Installing can delete a running version.**
  - `ComponentInstaller` deletes an existing target, even a version that is running. It must refuse
    or install side by side instead.
  - The version string must be validated before it is combined into a path.
  - The Agent must take the release URL and hash from its own catalog, not from what the window sends.
- **M3. Self-update deletes the user's files.** The update moves every top-level file of the app
  folder into `.update\previous-<v>`, then deletes the older copy. A user's own file at the app root
  is gone after two updates. Move only the files listed in the release manifest.
- **M4. A crash can truncate the hosts file.** `HostsHandler` writes it with `File.WriteAllText`.
  Write a temporary file and replace instead.
- **M6. Unescaped values in Apache directives.** Root, Index and the proxy URL reach the
  configuration unescaped: quotes, CR/LF and `%{` get through. Reject or escape them.
- **M8. A broken PHP update is not detected.** A php-cgi build that starts but crashes on its first
  request is not rolled back. Send one request through the pool before committing the update.
- **M9. The window writes `data\`.** It writes logs and temporary files there directly, against "the
  Agent is the only writer of `data\`". Fix it or reword the rule to match a deliberate split; the
  builder decides which, and says so.
- **M11. The Elevator can change settings.** It calls `SettingsStore.Load`, which can move a corrupt
  `anvil.json` aside. The elevated process must only read `data\`.

Also out of the log:
- M5 (nested mounts) disappears with mounts.
- M7 (the spec says 10 s where the code waits 15 s) is fixed while the documents are updated.
- M10 (child mode never checks folder permissions) stays deferred.

## Licence, version, documents

- `LICENSE`: the verbatim GPL-3.0 text from gnu.org. README badge and any licence metadata in project
  files follow. `CONTRIBUTING.md` says contributions are accepted under GPL-3.0. Check that every
  NuGet package shipped is under a licence GPL-3.0 can include (MIT, BSD, Apache 2.0 are fine).
  Apache, PHP and MariaDB/MySQL are downloaded by the user at run time, not shipped, so their
  licences do not bind the release.
- `Directory.Build.props`: `1.0.0`. `CHANGELOG.md`: one Anvil 1.0.0 entry replaces the 4.0.0 and 4.1.0
  entries, written for users, with no mention of features that never shipped. Date: "unreleased"
  until the owner releases.
- `README.md`, `SECURITY.md`, `CONTRIBUTING.md`, `packaging\README.txt`: development only, Anvil,
  network names explained, the colleague command shown.
- The 2026-10-27 design gets a line at the top pointing to this document.
- `docs/code-signing.md`: how to sign releases with SignPath Foundation, step by step: what
  SignPath Foundation requires of the project (checked against their current published terms when
  written), applying, the GitHub Actions steps that would submit the release build for signing (as
  text, not turned on), the certificate subject the updater's Authenticode check should then pin,
  and how to check a signed build.
- `docs/manual-test-checklist.html`: one self-contained page (no network needed), a checklist of
  every manual check: first run, each page, sites on This PC and My network, network names from a
  second machine with the NRPT command and its removal
  (`Get-DnsClientNrptRule | Where-Object Namespace -eq '.arbib.pc' | Remove-DnsClientNrptRule -Force`),
  port 53 busy, hosts and firewall prompts, local HTTPS trust, component install and rollback,
  database, logs, self-update end to end after a release, opening twice, start with Windows, a
  Defender and VirusTotal scan. Ticks are kept in the browser's local storage, with a reset button
  and a print view. It is also published as a private artifact for the owner.

## Constraints

- Follow `CLAUDE.md`: one version change (to 1.0.0 under the new name), changelog kept, conventional commits, no AI
  attribution lines.
- Work continues on branch `feat/arbipache4`.
- Tests stay green: tests of removed features are deleted with them; new behaviour gets tests first.
  The name service needs unit tests (packet parsing and building, which names get which answer,
  private-range filtering, malformed packets) and one integration test that sends real UDP queries to
  the responder on a test port. The responder's port is therefore configurable internally; only 53 is
  used outside tests.
- Leftover data from test runs must load without crashing: an IP site is skipped and listed in
  `SiteStore.Warnings`; `Everyone` and `SpecificIps` load as `Network` and `ThisPc`; a password flag
  is ignored; a certificate source other than `LocalDev` loads as `None`; unknown settings keys are
  ignored.
- `TreatWarningsAsErrors` stays on.

## Calls made by Claude (check these)

- Password protection and "Specific IPs" removed along with "Everyone": on a development PC shared
  over the local network they add more than they give.
- New sites default to My network.
- Network domain off until set. The placeholder suggests `<computer name>.internal`.
- Only the first label of a domain site's name becomes its network name. Aliases get none.
- Every name under the network domain resolves, claimed or not.
- A Mac command next to the Windows one.
- Development values written into `php.ini` and `my.ini` once instead of an overlay file.
- Xdebug becomes an ordinary extension.
- The name service starts and stops with Apache.
- The name service listens per LAN address and skips virtual adapters.
- Firewall rules are made per program, and Anvil replaces Windows' own prompt-made rules for its
  own programs.
- No UAC prompt on its own; a banner with an **Open firewall** button instead. Rules are never closed.

## Decided by the owner (2026-10-28)

- The product is renamed Anvil, in a new repository, starting at 1.0.0.
- IP and folder addresses stay; IP sites and `/demo` folders go.
- Network names under a domain the owner picks (`arbib.pc`), resolved by Anvil, set up once on a
  colleague's machine with `Add-DnsClientNrptRule`, with a copy button in the window.
- A Public network gets a warning and a button to Windows' settings, not a switch and not an
  exception.
- All eight non-production review leftovers are fixed in 1.0.0.
- Name Anvil, new repository github.com/ArbibSoufiane/Anvil, version 1.0.0, GPL-3.0, SignPath Foundation later, a guide for now.

## Left to the builder

- Whether the DNS responder is hand-written (expected: a few hundred lines, no new dependency) or uses
  a library.
- How the network category is read (Network List Manager COM or another read-only API).
- How the firewall rules are compared without admin rights (`HNetCfg.FwPolicy2` reads work unelevated).
- How network names are rendered (extra `ServerAlias`, or separate HTTP-only vhosts) as long as they
  never redirect to HTTPS and keep the site's access rule.
- Layout and wording of the Network sharing card and the Sites page addresses, in the mockup's tone.
- Order of removal, class names, and how tests are split.
- Whether `RenewalWorker` keeps a small job for local certificates or goes entirely (local
  certificates are already re-issued on save when they near expiry).
