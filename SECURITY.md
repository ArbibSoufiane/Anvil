# Security policy

## Supported versions

The newest release. I am the only maintainer, so there is no backporting to older versions — an
update is always the fix.

## Reporting a vulnerability

Please use GitHub's [private advisory form](https://github.com/ArbibSoufiane/ArbiPache/security/advisories/new)
rather than opening a public issue, and give me a reasonable window before you disclose.

ArbiPache can serve sites to the internet. It installs a Windows service, edits the hosts file,
opens firewall ports, changes folder permissions, adds a certificate authority to your trust store,
and downloads and runs Apache, PHP and database binaries. Bugs in any of those are worth telling me
about even if you are unsure they are exploitable.

## How it is put together

- **Three processes.** The window (`ArbiPache.exe`) runs as you, unelevated. The Agent
  (`ArbiPache.Agent.exe`) runs Apache, PHP and the database, either as the window's child or as the
  `ArbiPacheAgent` service under the virtual account `NT SERVICE\ArbiPacheAgent` — never SYSTEM. The
  Elevator (`ArbiPache.Elevator.exe`) is the only part that runs as administrator, for one request at
  a time from a fixed set: hosts entries, firewall rules, folder permissions, service install and
  removal.
- **The Agent's pipe** (`ArbiPache.Agent`) accepts only the account the Agent runs as,
  administrators, and the users the service was installed for.
- **The service refuses a writable folder.** ArbiPache will not install the service while accounts
  other than administrators and the installing user can change files in its folder, because they
  could replace `ArbiPache.Agent.exe` and run code as the service.
- **Downloads are HTTPS and SHA-256 checked before use**: against hashes pinned in
  `catalog\components.json`, or against the hashes PHP and MariaDB publish for their own builds.
- **Production mode** hides errors, cuts off long-running requests, sends `403` to unknown host
  names, and lists what it can see is still exposed: the database listening beyond this PC,
  phpMyAdmin open to everyone, sites without HTTPS.

## Known limitations, already documented

These are design constraints rather than findings. Tell me anyway if you like, but I will close
them as known:

**Releases are not code-signed yet.** The in-app updater checks a download against the
`SHA256SUMS` published in the same GitHub release. That proves the file arrived intact. It does not
prove the release is genuine, because the file and its checksum come from the same place: anyone
who could publish a release could publish a matching checksum. Signing is planned; once releases are
signed, the updater also refuses program files that do not carry the expected signature.

**The local development certificate authority.** HTTPS for `.test` sites comes from ArbiPache's own
certificate authority, trusted in your user's `CurrentUser\Root` store when you click Trust. Its
private key is `data\certs\ca\ca.key`, readable only by you, administrators and the service. Anyone
who can read that file can issue a certificate for any domain that your browsers will then trust. Settings → Remove from Windows takes the trust away.

**PHP runs as the Agent.** As a service, PHP scripts run as `NT SERVICE\ArbiPacheAgent`, which keeps
only the privileges it uses (no impersonation). A script can read and change what the service can:
`data\` (settings, certificate keys, the database password) and `runtime\`, besides the site
folders. A site with a hole in it therefore exposes those secrets, so keep internet-facing apps up
to date. ArbiPache runs no program named in `data\`: the editor you choose is kept in your own
profile.

**PHP pools listen on 127.0.0.1.** Apache reaches PHP over FastCGI on local TCP ports (9100 and up),
because Apache on Windows cannot use named pipes for it. Any program or account on this PC can
connect to those ports and run a PHP file the service can read, as the service. On a PC shared
with people you do not trust, do not run ArbiPache as a service.

**htdocs is served without a login.** Folders in `htdocs\` are never reachable from the internet,
but anyone on your network can open them when you allow that in Settings. Anything you serve that
way needs its own login. phpMyAdmin in particular is a database console: give it a password or set
it to "This PC only".

**Revocation is not checked** when the updater verifies a signature, so updates also work offline.
A revoked but unexpired signing certificate would still pass.
