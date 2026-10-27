# ArbiPache

[![ci](https://github.com/ArbibSoufiane/ArbiPache/actions/workflows/ci.yml/badge.svg)](https://github.com/ArbibSoufiane/ArbiPache/actions/workflows/ci.yml)
[![license](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

Apache, PHP and MariaDB/MySQL for Windows, in one portable folder, driven by a desktop app.
Use it on your own PC for development, or on a client's server where the requests come from the
internet.

- **Three kinds of site.** A domain site (`shop.test`, `shop.example.com`) with its own folder and
  PHP version. An IP site, served at `http://192.168.10.2/` with no domain, plus extra folders such as
  `/demo`. And every folder in `htdocs\`, at `http://localhost/<folder>`.
- **Several PHP versions at once**, each in its own pool of workers that uses every CPU core.
- **Development and Production modes.** One switch rewrites PHP, Apache and database limits, shows
  you every setting it changes first, and backs up every file it touches.
- **Per-site access and HTTPS.** Everyone, my network, this PC or specific IPs, with an optional
  password. HTTPS from Let's Encrypt, from a certificate a client sent you, from the client's load
  balancer, or from ArbiPache's local development certificate authority.
- **MariaDB or MySQL**, with a settings page that shows each setting's current value, default and
  meaning. Bring phpMyAdmin yourself and add it as a site.
- **Install, update and roll back** Apache, PHP and the database from the app.
- **Buttons that open** `httpd.conf`, `php.ini`, `my.ini` and every generated file in your editor.
  Those files stay yours: ArbiPache writes only its own marked blocks and generated files.

ArbiPache 4 is a rewrite of [ArbiPache 3](https://github.com/ArbibSoufiane/ArbiPache/tree/v4.1.0)
(Python) in C# / .NET 10 with an Avalonia window. There is no command-line interface.

## Installing

1. Download `ArbiPache-<version>-win64.zip` from
   [Releases](https://github.com/ArbibSoufiane/ArbiPache/releases) and check it against the
   `SHA256SUMS` file published with it.
2. Unzip it somewhere only you can change: a local disk, not Program Files, not a network share.
   `D:\ArbiPache` is fine.
3. Run `ArbiPache.exe`.

Windows 10 or 11, or Windows Server 2022, x64. Apache and PHP need the Microsoft Visual C++
Redistributable; ArbiPache tells you when it is missing. Nothing is installed outside the folder:
copy the folder to another disk or machine and everything comes with it.

## First run

1. **Apache page:** install a version.
2. **PHP page:** install one or more PHP versions and pick the default.
3. **Database page** (optional): install MariaDB or MySQL and choose its root password. ArbiPache
   keeps the password encrypted with Windows DPAPI; the database listens on this PC only until you
   change `bind-address`.
4. **Start.** Folders in `htdocs\` answer at `http://localhost/<folder>`.
5. **Sites → Add** for a domain site, an IP site or a proxy to a dev server such as Vite. ArbiPache
   recognises Laravel, Symfony, WordPress, Composer and Vite projects and suggests the right folder.

Opening `ArbiPache.exe` a second time brings the running window to the front. Closing the window
keeps ArbiPache in the tray unless you turn that off in Settings.

## Development and Production

| | Development | Production |
|---|---|---|
| PHP errors | shown | logged, not shown |
| PHP time / memory | 300 s / 1 GB | 30 s / 256 MB |
| Uploads | 512 MB | 32 MB |
| Apache timeout | 300 s | 60 s, slow requests cut off |
| Server banner | full | product only, no signature |
| Unknown host names | served from htdocs (private networks only) | `403`, empty |
| Firewall | private networks, local subnet | all networks |

The Mode page shows every value, lets you edit each profile, and shows the exact changes before a
switch. A switch backs up every touched file to `data\backups\`, config-tests, restarts only what
changed, and restores the backup if anything fails.

## Production checklist

ArbiPache's Dashboard lists what it can see from inside the machine: the database listening beyond
this PC, phpMyAdmin open to everyone, display_errors on, sites without HTTPS, ArbiPache not running
as a service. The rest is outside it:

- **Run as a Windows service** (Settings), so sites stay up after sign-out and restarts. The service
  runs as `NT SERVICE\ArbiPacheAgent`, not SYSTEM. ArbiPache refuses to install it while other
  accounts can change files in its folder.
- **Router:** forward TCP 80 and 443 to this machine's LAN address, and give the machine a fixed
  address (a DHCP reservation) so the forward does not break.
- **DNS:** point each domain's `A` record (and `AAAA`, if you serve IPv6) at the public address,
  before you ask for a Let's Encrypt certificate: the challenge comes in over port 80.
- **Behind a client's load balancer:** choose "HTTP only, the load balancer handles HTTPS" and list
  the balancer's addresses. Client addresses and `HTTPS=on` are trusted only from those addresses.
- **Windows Update** stays on, and the machine restarts for it. With the service, sites come back
  by themselves.
- **phpMyAdmin:** set it to "This PC only" or "My network only", or give it a password.
- **Backups:** the Database page dumps every database (14 kept). Copy `data\backups\` off the machine.

## Two things Windows does not have

**Apache's Event MPM does not exist on Windows.** Windows has one MPM, WinNT: one process with many
threads, which already uses every core. The Apache page sets its threads per child, connections per
child and keep-alive.

**PHP-FPM does not exist on Windows.** ArbiPache runs the same model: per PHP version, one listening
socket shared by a pool of `php-cgi.exe` workers that are restarted when they crash and recycled
after a number of requests. Apache reaches the pool through `mod_proxy_fcgi`. The PHP page sets the
pool size and recycling.

## Updating

**ArbiPache:** Settings → Check for updates. Nothing is checked until you ask. Installing downloads
the release, checks it against its `SHA256SUMS`, stops your sites, swaps the program files and opens
the new version. `runtime\`, `data\` and `htdocs\` are never touched; the replaced files wait in
`.update\previous-<version>\` and are put back if the swap fails.

**Apache, PHP, the database:** their own pages offer patch releases of the versions you use. A new
Apache that does not answer within 15 seconds is switched back automatically; a new PHP keeps your
`php.ini`.

## Antivirus and SmartScreen

ArbiPache does things that look like malware from the outside: it asks for administrator rights,
installs a service, edits the hosts file, opens firewall ports, adds a certificate authority to your
trust store and downloads executables. That is the job. The build avoids what makes antivirus
heuristics worse: no single-file exe that unpacks itself, no trimming, no packers, full version
information, no helper executables downloaded at run time, and administrator work isolated in
`ArbiPache.Elevator.exe` with a fixed set of requests.

Until releases are code-signed, SmartScreen may say "Windows protected your PC". If you downloaded
the zip from the Releases page and its checksum matches, choose More info → Run anyway. If Defender
flags a file, report it at <https://www.microsoft.com/en-us/wdsi/filesubmission> rather than
turning Defender off.

If you did not build this yourself and do not trust where you got it, delete it rather than add an
exclusion. I mean that.

## Folder layout

```
ArbiPache\
  ArbiPache.exe  ArbiPache.Agent.exe  ArbiPache.Elevator.exe  *.dll
  catalog\        download catalog: URLs and SHA-256 hashes
  runtime\        installed Apache, PHP, MariaDB/MySQL versions
  htdocs\         your folders
  data\           settings, sites\*.json, httpd.conf, php.ini, my.ini, certificates, logs, backups
```

`ArbiPache.exe` is the window. `ArbiPache.Agent.exe` runs Apache, PHP and the database, as the
window's child process or as the Windows service. `ArbiPache.Elevator.exe` does the few things that
need administrator rights: the hosts file, firewall rules, folder permissions and the service.

## Building from source

Needs the .NET 10 SDK (see `global.json`) on Windows x64.

```powershell
dotnet build
dotnet test --project tests/ArbiPache.Core.Tests
dotnet test --project tests/ArbiPache.App.Tests
dotnet run --project src/ArbiPache.App
```

`build\publish.ps1` builds a release into `dist\`: it refuses uncommitted changes and a version
without a dated `CHANGELOG.md` section, runs the tests, and writes the zip and `SHA256SUMS`. Pushing
a `v<version>` tag runs it on GitHub Actions and publishes the release. See
[CONTRIBUTING.md](CONTRIBUTING.md).

## License

[Apache 2.0](LICENSE). The IBM Plex fonts are under the SIL Open Font License.
