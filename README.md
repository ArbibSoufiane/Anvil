# Anvil

[![ci](https://github.com/ArbibSoufiane/Anvil/actions/workflows/ci.yml/badge.svg)](https://github.com/ArbibSoufiane/Anvil/actions/workflows/ci.yml)
[![license: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue.svg)](LICENSE)

Apache, PHP and MariaDB/MySQL for Windows, in one portable folder, driven by a desktop app. Anvil is
for development on your own PC, and for showing your work to the people on your network. It is not a
production server.

- **Your folders, at once.** Every folder in `htdocs\` answers at `http://localhost/<folder>`.
- **Domain sites** such as `shop.test`, with their own folder, PHP version and HTTPS from Anvil's
  local certificate authority.
- **Several PHP versions at once**, each in its own pool of workers that uses every CPU core.
- **Share with your network.** Open a site to My network and others reach it at
  `http://<your IP>/<folder>` or `<app>.<your domain>`; Anvil answers those names itself.
- **MariaDB or MySQL**, with a settings page that shows each setting's current value, default and
  meaning. Bring phpMyAdmin yourself and add it as a site.
- **Install, update and roll back** Apache, PHP and the database from the app.
- **Buttons that open** `httpd.conf`, `php.ini`, `my.ini` and every generated file in your editor.
  Those files stay yours: Anvil writes only its own marked blocks and generated files.

Anvil is the new name of ArbiPache 4, a rewrite of
[ArbiPache 3](https://github.com/ArbibSoufiane/ArbiPache/tree/v4.1.0) (Python) in C# / .NET 10 with an
Avalonia window. There is no command-line interface.

## Installing

1. Download `Anvil-<version>-win64.zip` from
   [Releases](https://github.com/ArbibSoufiane/Anvil/releases) and check it against the
   `SHA256SUMS` file published with it.
2. Unzip it somewhere only you can change: a local disk, not Program Files, not a network share.
   `D:\Anvil` is fine.
3. Run `Anvil.exe`.

Windows 10 or 11, x64. Apache and PHP need the Microsoft Visual C++ Redistributable; Anvil tells you
when it is missing. Nothing is installed outside the folder: copy the folder to another disk or PC
and everything comes with it.

## First run

1. **Apache page:** install a version.
2. **PHP page:** install one or more PHP versions and pick the default. Xdebug is an extension you
   switch on there.
3. **Database page** (optional): install MariaDB or MySQL and choose its root password. Anvil keeps
   the password encrypted with Windows DPAPI.
4. **Start.** Folders in `htdocs\` answer at `http://localhost/<folder>`.
5. **Sites → Add** for a domain site or a proxy to a dev server such as Vite. Anvil recognises
   Laravel, Symfony, WordPress, Composer and Vite projects and suggests the right folder.

Coming from ArbiPache 3? Settings → Import from ArbiPache 3 brings your sites over.

Opening `Anvil.exe` a second time brings the running window to the front. Closing the window keeps
Anvil in the tray unless you turn that off in Settings.

## Three ways to open a site

**On this PC: `http://localhost/<folder>`.** Every folder in `htdocs\`, with no setup.

**On this PC: a `.test` domain.** Sites → Add gives a folder its own name such as `shop.test`.
Anvil adds it to the hosts file (Windows asks for permission) and can serve it over HTTPS with a
certificate from its own certificate authority: click Trust once and your browsers accept it.

**From another device on your network.** Set the site, or the `htdocs` folders, to **My network**.
Then others open:

- `http://<your IP>/<folder>` for a folder in `htdocs\`, for example `http://192.168.1.5/shop`;
- `http://<app>.<your domain>/` for a site or folder, for example `http://shop.arbib.pc/`, once you set
  a network domain (below).

A site set to This PC answers only on this PC, whatever the network settings.

### Network names and the colleague command

Settings → Network sharing → **Network domain**: pick a name that is not a real internet domain,
such as `arbib.pc`. While Apache runs, Anvil answers name lookups for everything under it with this
PC's address. A domain site gets its first label (`shop.test` answers at `shop.arbib.pc`), a folder
its own name.

A colleague's PC does not know to ask your PC about `arbib.pc` yet. The same card shows the command
that tells it, with a copy button. On Windows, they run it once in PowerShell as administrator:

```powershell
Add-DnsClientNrptRule -Namespace ".arbib.pc" -NameServers 192.168.1.5
```

On macOS:

```sh
sudo mkdir -p /etc/resolver && echo "nameserver 192.168.1.5" | sudo tee /etc/resolver/arbib.pc
```

To undo it on Windows:

```powershell
Get-DnsClientNrptRule | Where-Object Namespace -eq '.arbib.pc' | Remove-DnsClientNrptRule -Force
```

and on macOS, `sudo rm /etc/resolver/arbib.pc`.

The command names your PC's address, so give your PC a fixed address (a DHCP reservation in your
router); otherwise colleagues run it again when the address changes. If they would rather not run a
command, they can add one hosts line per name instead, such as `192.168.1.5 shop.arbib.pc`.

### Public networks and the firewall

Windows keeps other devices out on a network it treats as **Public**. When a site is open to your
network and Windows calls the network Public, the Dashboard says so and offers **Open network
settings**: choose your connection, then Network profile type → **Private**. Only do that on a
network you trust, such as your home or office.

When the firewall does not let other devices in yet, the Dashboard offers **Open firewall**.
Windows asks for permission, then Anvil adds rules for Apache's ports and its name service,
for Private and Domain networks and your local subnet only.

## Updating

**Anvil:** Settings → Check for updates. Nothing is checked until you ask. Installing downloads the
release, checks it against its `SHA256SUMS`, stops your sites, swaps Anvil's own program files and
opens the new version. `runtime\`, `data\`, `htdocs\` and files you keep next to `Anvil.exe` are
never touched; the replaced files wait in `.update\previous-<version>\` and are put back if the swap
fails.

**Apache, PHP, the database:** their own pages offer patch releases of the versions you use. A new
Apache that does not answer within 15 seconds is switched back automatically, and so is a new PHP
that cannot answer a request. A new PHP keeps your `php.ini`.

## Two things Windows does not have

**Apache's Event MPM does not exist on Windows.** Windows has one MPM, WinNT: one process with many
threads, which already uses every core. The Apache page sets its threads per child, connections per
child and keep-alive.

**PHP-FPM does not exist on Windows.** Anvil runs the same model: per PHP version, one listening
socket shared by a pool of `php-cgi.exe` workers that are restarted when they crash and recycled
after a number of requests. Apache reaches the pool through `mod_proxy_fcgi`. The PHP page sets the
pool size and recycling.

## Antivirus and SmartScreen

Anvil does things that look like malware from the outside: it asks for administrator rights, edits
the hosts file, opens firewall ports, answers name lookups, adds a certificate authority to your
trust store and downloads executables. That is the job. The build avoids what makes antivirus
heuristics worse: no single-file exe that unpacks itself, no trimming, no packers, full version
information, no helper executables downloaded at run time, and administrator work isolated in
`Anvil.Elevator.exe` with a fixed set of requests.

Until releases are code-signed, SmartScreen may say "Windows protected your PC". If you downloaded
the zip from the Releases page and its checksum matches, choose More info → Run anyway. If Defender
flags a file, report it at <https://www.microsoft.com/en-us/wdsi/filesubmission> rather than
turning Defender off.

If you did not build this yourself and do not trust where you got it, delete it rather than add an
exclusion. I mean that.

## Folder layout

```
Anvil\
  Anvil.exe  Anvil.Agent.exe  Anvil.Elevator.exe  *.dll  release-files.txt
  catalog\        download catalog: URLs and SHA-256 hashes
  runtime\        installed Apache, PHP, MariaDB/MySQL versions
  htdocs\         your folders
  data\           settings, sites\*.json, httpd.conf, php.ini, my.ini, certificates, logs, backups
```

`Anvil.exe` is the window. `Anvil.Agent.exe` runs Apache, PHP, the database and the name service as
the window's child process. `Anvil.Elevator.exe` does the two things that need administrator rights:
the hosts file and firewall rules.

## Building from source

Needs the .NET 10 SDK (see `global.json`) on Windows x64.

```powershell
dotnet build
dotnet test --project tests/Anvil.Core.Tests
dotnet test --project tests/Anvil.App.Tests
dotnet run --project src/Anvil.App
```

`build\publish.ps1` builds a release into `dist\`: it refuses uncommitted changes and a version
without a dated `CHANGELOG.md` section, runs the tests, and writes the zip and `SHA256SUMS`. Pushing
a `v<version>` tag runs it on GitHub Actions and publishes the release. See
[CONTRIBUTING.md](CONTRIBUTING.md).

## License

Anvil is free software under the [GNU General Public License v3.0](LICENSE): you may use it, read its
source, change it and share it. If you share Anvil or a changed version, you share it under the same
licence, with its source. The IBM Plex fonts are under the SIL Open Font License.
