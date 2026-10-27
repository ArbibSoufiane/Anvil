# Contributing to ArbiPache

ArbiPache 4 is a rewrite of a tool I built for my own work, in C# this time, and it still serves my
needs first. The architecture is deliberate, the test suite is real, and help is welcome. This is
what I think you need to know first.

## Before you start

You need a Windows machine, x64. ArbiPache manages Windows services, the hosts file, the firewall,
folder permissions and the certificate store, and it is not tested on ARM64.

| | |
|---|---|
| **Windows 10 or 11** | x64. Administrator rights only for the Elevator's work. |
| **.NET 10 SDK** | The version in `global.json`, or a later feature band. |
| **Git** | |
| **A free port 80** | Only for the integration tests and for running it. IIS, Docker Desktop, VMware and Web Deploy all want port 80; the Dashboard names whichever one holds it. |

Apache, PHP and MariaDB/MySQL are **not** prerequisites. ArbiPache downloads them itself, checksum
verified, from the Apache, PHP and Database pages.

## Getting set up

```powershell
git clone https://github.com/ArbibSoufiane/ArbiPache.git
cd ArbiPache
dotnet build

dotnet test --project tests/ArbiPache.Core.Tests     # seconds
dotnet test --project tests/ArbiPache.App.Tests      # headless Avalonia, seconds
dotnet run --project src/ArbiPache.App               # the app, from source
```

One class at a time: `dotnet test --project tests/ArbiPache.Core.Tests -- --filter-class "*SiteOperationsTests"`.

`tests/ArbiPache.Integration.Tests` runs real Apache, PHP and database binaries in throwaway folders.
It downloads them once into `%TEMP%\arbipache-it-cache` and needs the network the first time:
`dotnet test --project tests/ArbiPache.Integration.Tests`.

A copy run from `bin\Debug` or `bin\Release` keeps its `runtime\`, `data\` and `htdocs\` next to the
build output, so it never touches an ArbiPache you use for real work. It also refuses to update itself.

## How the code is laid out

```
src/ArbiPache.Core/       all the logic: settings, sites, Apache/PHP/DB config, downloads, updates. No UI.
src/ArbiPache.Agent/      the process that runs Apache, PHP and the database; the Orchestrator owns every change
src/ArbiPache.Elevator/   the only code that runs as administrator, one request at a time from a fixed set
src/ArbiPache.App/        the Avalonia window: view models, views, and the pipe client
catalog/components.json   pinned download URLs and SHA-256 hashes
tests/                    Core and App unit tests, integration tests, a child-process helper
```

Five rules hold the design together. I will ask you to keep them, because breaking one is how this
becomes unmaintainable:

**`data\sites\*.json` is the source of truth.** Everything under `data\conf\` is generated from the
site definitions and rebuilt on demand. Never write a vhost by hand and never read one back.

**The user's files stay the user's.** `httpd.conf`, `php.ini` and `my.ini` belong to the user.
ArbiPache writes only its own marked blocks and its own generated files, and config-tests every
change before it takes effect.

**Every change goes through the Agent's Orchestrator.** The window asks over the `ArbiPache.Agent`
pipe (`IAgentApi`); the Orchestrator runs one operation at a time, config-tests, and rolls back on
failure. The window never writes `data\` itself.

**Administrator work goes through the Elevator.** A new privileged action is a new request type in
`ArbiPache.Core.Elevation` with its own handler, not a `runas` from somewhere else. Firewall rules go
through COM and services through the Win32 API: no `netsh advfirewall`, no `sc.exe`.

**Nothing leaves the folder.** No `%USERPROFILE%` state, no registry beyond the optional Run key and
the optional service. Copying the folder must be enough to move ArbiPache.

## Making a change

Work on a branch off `main`, named for what it does: `feat/mail-catcher`, `fix/cert-expiry`.

**Write the test first.** If a pull request adds behaviour and nothing in the suite fails without it,
I will ask you for a test.

**Bump the version, once per change set,** in `Directory.Build.props`, the only place it is
defined. Semantic versioning: a feature bumps the minor, a fix bumps the patch, an incompatible change
bumps the major.

**Add a `CHANGELOG.md` entry** under the new version, written for the person using ArbiPache rather
than the person who wrote the patch.

**Commit messages** use conventional prefixes: `feat:`, `fix:`, `test:`, `chore:`, `docs:`.

**Run both unit suites before you push.** `build\publish.ps1` refuses to package a failing tree, so
a red suite blocks releases, not just your branch.

Then open a pull request describing what problem it solves. Screenshots help for anything that
changes the window.

## Reporting a bug

Attach the logs from `data\logs\arbipache\` (`app-*.log`, `agent-*.log`) and say which page you were
on. Say what you expected and what happened instead. "Site 500s after switching PHP version" is a
report; "doesn't work" is not.

## Reporting a security problem

Please tell me privately first, through GitHub's
[private advisory form](https://github.com/ArbibSoufiane/ArbiPache/security/advisories/new).
[SECURITY.md](SECURITY.md) is the full policy, including the limitations already known.

## Licensing your contribution

ArbiPache is [Apache 2.0](LICENSE). Opening a pull request means you are offering your change under
that same license, and that you have the right to do so.

If you paste code from somewhere else, tell me where it came from and under what license. Apache 2.0
can absorb MIT and BSD; it cannot absorb GPL.
