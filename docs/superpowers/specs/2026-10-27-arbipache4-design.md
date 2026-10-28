# ArbiPache 4 — Design

Date: 2026-10-27 · Status: approved in conversation · UI mockup: https://claude.ai/artifact/Gt31veQvjjeYTFPVJZhHdt

## Intent

ArbiPache replaces XAMPP-style stacks with something simple, portable and fully under the
owner's control. Version 3 (Python + PySide6, https://github.com/ArbibSoufiane/ArbiPache) works,
but Windows Defender flags it, it has a CLI nobody needs, and it lacks a database, PHP management,
production hardening and a good UI. Version 4 is a rewrite in C# / .NET with an Avalonia GUI.

It is used two ways:
- **Development** on the owner's PC: many sites, `.test` domains, every error visible.
- **Production** on client servers: requests come **from the internet**, the machine serves real
  users, errors are hidden and runaway work is cut off.

## Goals

1. No Windows Defender / SmartScreen detection of the shipped app.
2. GUI only (no CLI). Polished, easy, matches the mockup.
3. Opening the app twice brings the running window to the front; nothing starts twice.
4. Three site kinds: **domain site**, **IP site** ("single site" mode: `http://192.168.10.2/` serves
   one site, plus extra folders like `/demo`), **htdocs folder** (`localhost/<folder>`, network IP).
5. Install, update, switch and roll back Apache, PHP and MariaDB/MySQL versions from the app.
6. Use every CPU core: Apache WinNT MPM thread tuning and an FPM-style PHP worker pool.
7. Buttons that open `httpd.conf`, `php.ini`, `my.ini` and generated files in the user's editor.
8. One switch between Development and Production that rewrites PHP, Apache and DB limits.
9. MariaDB or MySQL with a settings view that shows current value, default and meaning.
10. Per site: who can open it (everyone / my network / this PC / specific IPs, optional password)
    and where HTTPS comes from (Let's Encrypt / certificate a client sent / HTTP only behind the
    client's load balancer / local development certificate).
11. Fully portable: copy the folder to another disk or machine and it works.

## Non-goals

- No CLI, no scripting surface for users.
- No bundled phpMyAdmin or Adminer. The owner adds phpMyAdmin as an ordinary site.
- No Linux or macOS. No ARM64 (Apache Lounge publishes no ARM64 builds).
- No mail catcher, no wildcard DNS resolver, no Node/Python runtimes (proxy sites only).
- No light theme in 4.0.

## Anti-goals (failure even if it "works")

- Owning the user's files: `httpd.conf`, `php.ini`, `my.ini` stay the user's; ArbiPache only
  writes its marked blocks and its generated files.
- A config error that takes running sites down: every change is config-tested first and rolled back.
- Serving htdocs, the database or phpMyAdmin to the internet by accident.
- Apache or PHP serving internet traffic under the SYSTEM account.

## Platform limits stated to the user

- **Apache Event MPM does not exist on Windows.** Windows has only the WinNT MPM: one process,
  many threads, all cores. ArbiPache exposes `ThreadsPerChild`, `MaxConnectionsPerChild`, KeepAlive.
- **PHP-FPM does not exist on Windows.** ArbiPache runs the same model as FPM: per PHP version,
  the Agent owns one listening socket on `127.0.0.1:<port>`, and N supervised `php-cgi.exe`
  workers inherit it and `accept()` on it, restarted on crash and recycled after N requests.
  Apache reaches the pool with `mod_proxy_fcgi`; there is no `mod_proxy_balancer`.
  Decided by the Phase 2 spike (Apache 2.4.69, PHP 8.5.12 NTS); the details follow.
  - **Why one shared socket.** The first design, one `php-cgi -b <port>` per worker behind a
    balancer, failed under load:
    - Each recycle closed the worker's port, and the requests queued on it got 503.
    - When a worker died mid-request, the balancer re-sent the request to another worker: one
      POST ran twice and its client hung until the backend timeout.
    - Turning failover off (`maxattempts=0`) stopped the replays, but 26 of 300 burst requests
      still failed across 4 recycles.

    With the shared socket, the backlog belongs to the Agent and survives worker restarts:
    - Recycling every 5 requests under a 20-way POST burst gave 300 of 300 OK.
    - A worker killed mid-request gives exactly that request a 503, in under 0.1 s. Nothing is
      replayed and the next requests succeed.
  - **Worker launch.** stdin is the inheritable listen socket, passed through
    `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`. stdout and stderr must both be `INVALID_HANDLE_VALUE`:
    php-cgi enters FastCGI mode only then. With stderr redirected it falls back to CGI and hangs.
    Workers therefore write only to PHP's `error_log` file; nothing is captured from stderr.
  - **Apache directives Windows needs:**
    - The handler URL needs a trailing slash: `SetHandler "proxy:fcgi://127.0.0.1:<port>/"`.
      Without it, Apache appends `C:/…` straight after the port, and the request fails with
      "No protocol handler was valid".
    - `ProxyFCGIBackendType GENERIC` plus
      `ProxyFCGISetEnvIf "reqenv('SCRIPT_FILENAME') =~ m#^/([A-Za-z]:/.*)$#" SCRIPT_FILENAME "$1"`.
      The default FPM type sends `proxy:fcgi://…/C:/…`, and GENERIC sends `/C:/…`. php-cgi rejects
      both ("No input file specified").
    - `<Proxy "fcgi://127.0.0.1:<port>/">` with `ProxySet timeout=<PhpBackendTimeout> retry=0`.
      Without `retry=0`, one failed connection takes the pool out for 60 s.
  - **Verified:** `SCRIPT_FILENAME`, `SCRIPT_NAME`, `PATH_INFO` (`/index.php/foo/bar`) and
    `DOCUMENT_ROOT` are correct. Laravel's `public/.htaccess` front-controller rewrite works: `/`,
    `/login`, 404 routes, and the trailing-slash 301. A 100 MB multipart upload works.
    20 parallel `sleep(2)` requests spread over every worker.
  - **Fallback:** `mod_fcgid` (what v3 uses) stays the fallback, used only if socket inheritance
    fails in some environment. It is not shipped with the Apache build and is not implemented.

## Architecture

Three executables, one shared library, all in the app folder:

- **ArbiPache.exe** (Avalonia GUI, `asInvoker`, never elevated). Control panel. Talks to the Agent.
- **ArbiPache.Agent.exe** (headless). Owns everything that runs: renders config, supervises
  httpd / php-cgi pools / mariadbd, renews certificates, rotates logs. It is the **only writer** of
  `data/`. Runs either as a child process started by the GUI (default, Development) or as a Windows
  service under the virtual account `NT SERVICE\ArbiPacheAgent` (Production: survives reboot with
  nobody logged in). All its children live in a Job Object so none are orphaned.
- **ArbiPache.Elevator.exe** (`requireAdministrator`). Tiny. Started through UAC only when needed.
  Accepts one typed JSON request file per run with a fixed verb set: `hosts-set`, `firewall-set`,
  `service-install`, `service-uninstall`, `acl-grant`, `folder-secure` (its own folder only). Rejects
  anything else.
- **ArbiPache.Core** (library): all logic, pure where possible, unit tested.

httpd runs as a console process under the Agent, never as its own Windows service. `httpd -k restart`
and `-k shutdown` only reach an installed service ("No installed service named Apache2.4"), so the
Agent signals the console httpd through the named events `ap<pid>_restart` (graceful reload: the old
child finishes its requests while a new one starts) and `ap<pid>_shutdown`. If the event is missing or
httpd is not serving 10 s after a reload, the Agent stops and starts it. (Found in Phase 1, Task 1.10.)

GUI ↔ Agent: JSON-RPC (StreamJsonRpc) over the named pipe `ArbiPache.Agent`, ACL = installing user +
Administrators (+ the service SID). One Agent per machine (mutex `Global\ArbiPache.Agent`).

## Folder layout (portable root)

```
ArbiPache\
  ArbiPache.exe, ArbiPache.Agent.exe, ArbiPache.Elevator.exe, *.dll
  catalog\components.json        shipped download catalog (URLs + SHA-256)
  runtime\apache\<ver>\           downloaded, read-only payloads
  runtime\php\<ver>\
  runtime\mariadb\<ver>\  runtime\mysql\<ver>\
  htdocs\
  data\
    arbipache.json                app settings
    sites\<id>.json               site definitions (source of truth)
    apache\httpd.conf             user-owned; ArbiPache adds Define SRVROOT + one marked Include
    conf\                         GENERATED, fully regenerable (arbipache.conf, mode.conf, vhosts\…)
    php\<ver>\php.ini             user-owned; conf.d\99-arbipache-mode.ini generated
    mariadb\my.ini + datadir      (or mysql\)
    certs\  acme\  auth\  logs\  backups\  run\  tmp\  secrets\
    .root                         last known root path; mismatch ⇒ regenerate all config
```

Nothing is stored in `%USERPROFILE%`. Registry use is limited to the optional HKCU Run key and the
optional service registration.

## Sites

| Kind | Address | Notes |
|---|---|---|
| Domain | `shop.example.com`, `shop.test` | any folder; aliases; own PHP version |
| IP site | `192.168.10.2` or "all addresses" | replaces the htdocs listing on that address; extra folders (`/demo`, `/staging`), each with own folder + PHP version |
| htdocs folder | `localhost/<folder>`, `<ip>/<folder>` | never reachable from the internet |

Types: PHP, static, proxy (dev servers such as Vite, with websocket upgrade). Project detection:
Laravel/Symfony → `public\`, WordPress, Composer, static, Vite (`package.json`) → proxy `:5173`.

**Virtual host rule:** every vhost is `<VirtualHost *:{port}>` with `ServerName`. Never an
IP-specific `<VirtualHost 1.2.3.4:80>` — it would hide every name-based site from requests arriving
on that IP. Order: `00-catchall` (first = default server), `01-localhost`, then sites.

Catch-all (unknown `Host`): Development → htdocs (private ranges only). Production → `403`, empty
body. An IP site with "all addresses" becomes the catch-all.

**Who can open it** (per site): Everyone · My network only (loopback + RFC 1918 + ULA/link-local) ·
This PC only (`Require local`) · Specific IPs. Optional username/password (bcrypt htpasswd).
htdocs folders cannot be "Everyone".

**HTTPS** (per site):
- Let's Encrypt (ACME HTTP-01, automatic renewal). Not for IP sites or `.test`/`.local` names.
- Certificate I was given: `.pfx` (+password) or `.crt` + `.key` (+chain). Validated on import
  (key matches, not expired, names covered). Warnings at 30/7/1 days.
- HTTP only, client's load balancer terminates TLS: `mod_remoteip` trusts `X-Forwarded-For` only
  from the listed LB IPs; `HTTPS=on` set only when the TCP peer is a listed LB and
  `X-Forwarded-Proto` is `https`. Option: accept connections only from the LB IPs.
- Local development certificate: ArbiPache's own CA (no mkcert.exe), trusted in CurrentUser\Root.

## Modes (pinned values)

| Where | Setting | Development | Production |
|---|---|---|---|
| PHP | display_errors / display_startup_errors | On / On | Off / Off |
| PHP | error_reporting | E_ALL | E_ALL & ~E_DEPRECATED |
| PHP | log_errors | On | On |
| PHP | max_execution_time / max_input_time | 300 / 300 | 30 / 60 |
| PHP | memory_limit | 1G | 256M |
| PHP | upload_max_filesize / post_max_size | 512M / 512M | 32M / 32M |
| PHP | opcache.validate_timestamps / revalidate_freq | 1 / 0 | 0 / — |
| PHP | expose_php | On | Off |
| PHP | disable_functions | (empty) | exec,shell_exec,system,passthru,proc_open,popen |
| PHP | session.use_strict_mode / session.cookie_httponly | 0 / 0 | 1 / 1 |
| PHP | xdebug | loaded (if installed) | not loaded |
| Apache | Timeout | 300 | 60 |
| Apache | RequestReadTimeout | header=0 body=0 | header=20-40,MinRate=500 body=20,MinRate=500 |
| Apache | LimitRequestBody | 536870912 | 33554432 |
| Apache | ServerTokens / ServerSignature / TraceEnable | Full / On / On | Prod / Off / Off |
| Apache | Options Indexes (htdocs) | On | Off |
| Apache | Security headers | none | X-Content-Type-Options nosniff; X-Frame-Options SAMEORIGIN; Referrer-Policy strict-origin-when-cross-origin |
| Apache | LogLevel | info | warn |
| Apache | PHP backend timeout | 300 | 60 |
| DB | max_statement_time (MariaDB, s) / max_execution_time (MySQL, ms) | 0 | 60 / 60000 |
| DB | wait_timeout | 28800 | 600 |
| DB | general_log | ON | OFF |
| DB | slow_query_log / long_query_time | ON / 2 | ON / 5 |
| Firewall | 80/443 rule scope | Private+Domain profiles, local subnet | all profiles, any address |
| Catch-all | unknown Host | htdocs (private ranges) | 403 empty |

Each profile is editable. Switching shows the diff, backs up every touched file to
`data\backups\<timestamp>\`, writes, config-tests, restarts only what changed, and restores the
backup on failure. Switching back restores user files byte-identical.

## Database

MariaDB (default, 11.8 LTS) or MySQL (8.4 LTS), one active at a time. Portable zips. Initialized in
`data\<engine>\`. `bind-address=127.0.0.1` by default. Root password stored with DPAPI
(LocalMachine scope) in `data\secrets\`, file ACL limited to Administrators, the installing user and
the service SID. Settings view: key, current, default, description, Reset; "only changed" filter;
save + restart. Backups with `mariadb-dump`/`mysqldump` (gzip, keep 14).

## Windows Security strategy

1. No PyInstaller: .NET self-contained **folder** publish, no single-file, no trimming, no packers,
   full version resources.
2. Authenticode-sign every exe and our DLLs (Azure Artifact Signing if eligible, else SignPath
   Foundation for OSS, else Certum Open Source certificate), RFC 3161 timestamp.
3. No downloaded helper executables (mkcert dropped; certificates made in C#). Firewall via COM,
   services via Win32 API — no `netsh`/`sc.exe` spawning from the GUI.
4. GUI unelevated; admin work isolated in the signed Elevator with a fixed verb set.
5. Every download HTTPS + SHA-256 verified before use (catalog pins, or upstream-published hashes for
   PHP/MariaDB).
6. Scan every release build with Defender before publishing; submit false positives to Microsoft WDSI.

## Single instance

Mutex `Local\ArbiPache.App`. A second launch connects to pipe `ArbiPache.App.<user SID>`, receives
the first instance's PID, calls `AllowSetForegroundWindow(pid)`, sends `activate`, exits. The first
instance restores/shows/activates its window (also when hidden in the tray). `ERROR_ACCESS_DENIED`
on the mutex counts as "already running".

## Updates and migration

- Components: side-by-side install, switch with config test, auto-rollback if Apache is not healthy
  within 15 s; old version kept until removed.
- App: GitHub release zip + `SHA256SUMS` (+ Authenticode check once signed), staged, swapped by
  `ArbiPache.exe --apply-update`; `runtime\`, `data\`, `htdocs\` untouched.
- Import from ArbiPache 3: reads `%ARBIPACHE_HOME%` or `%USERPROFILE%\.arbipache\sites\*.json`.

## Calls made by Claude (check these)

- New codebase in `D:\Projects\ProductionServerManager`, product name ArbiPache, version 4.0.0.
- Target OS: Windows 10 22H2+, Windows 11, Windows Server 2019/2022/2025, x64.
- Agent architecture (child process in Development, Windows service in Production).
- Let's Encrypt and local development certificates added next to the two options the owner asked for.
- One mode per machine.
- Adminer dropped (owner brings phpMyAdmin); the mockup's Adminer buttons are removed.
- ModSecurity + OWASP CRS as an optional, late extra.
- Dark theme only; IBM Plex Sans/Mono fonts; orange accent.

## Left to the builder

Exact NuGet versions (latest stable at Phase 0, pinned centrally), visual details not in the mockup,
log viewer performance approach, wording of error messages (plain language, mockup tone).
