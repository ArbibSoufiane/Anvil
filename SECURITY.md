# Security policy

## Supported versions

The newest release. I am the only maintainer, so there is no backporting to older versions — an
update is always the fix.

## Scope

Anvil is a development tool for your own PC and your local network. It is not built to face the
internet, and it does not try to: it shows PHP errors, allows long requests and large uploads, and
serves `htdocs` folders without a login. Do not forward ports to it from your router.

What it does protect is the boundary between this PC, your local network and everything else:

- A site set to **This PC** answers only to this PC. A site set to **My network** answers only to
  loopback and private addresses (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `fc00::/7`,
  `fe80::/10`), even when an app's `.htaccess` allows everyone.
- The **name service** answers only queries from loopback and private addresses, and only for names
  under your network domain. Any other domain is refused, so it cannot be used to resolve the rest
  of the internet.
- The **firewall rules** Anvil adds apply to Private and Domain networks and your local subnet only.
  On a network Windows treats as Public, Anvil warns rather than opening anything.

Bugs that break one of those promises are what I most want to hear about.

## Reporting a vulnerability

Please use GitHub's [private advisory form](https://github.com/ArbibSoufiane/Anvil/security/advisories/new)
rather than opening a public issue, and give me a reasonable window before you disclose.

Anvil edits the hosts file, opens firewall ports, answers name lookups on UDP port 53, adds a
certificate authority to your trust store, and downloads and runs Apache, PHP and database binaries.
Bugs in any of those are worth telling me about even if you are unsure they are exploitable.

## How it is put together

- **Three processes.** The window (`Anvil.exe`) runs as you, unelevated. The Agent
  (`Anvil.Agent.exe`) runs Apache, PHP, the database and the name service as the window's child
  process, also as you. The Elevator (`Anvil.Elevator.exe`) is the only part that runs as
  administrator, for one request at a time from a fixed set: hosts entries and firewall rules. It
  reads requests only from `data\tmp\elevation\`, refuses that folder when it is a link elsewhere,
  and checks every value before it changes anything.
- **The Agent's pipe** (`Anvil.Agent`) accepts only your account and administrators.
- **Values that reach Apache's configuration are checked twice:** when a site is saved and again
  when the configuration is written, so a quote, line break or `${...}` in a hand-edited site file
  cannot add directives.
- **Downloads are HTTPS and SHA-256 checked before use**: against hashes pinned in
  `catalog\components.json`, or against the hashes PHP and MariaDB publish for their own builds. Only
  versions listed in the catalog can be installed.

## Known limitations, already documented

These are design constraints rather than findings. Tell me anyway if you like, but I will close
them as known:

**Releases are not code-signed yet.** The in-app updater checks a download against the
`SHA256SUMS` published in the same GitHub release. That proves the file arrived intact. It does not
prove the release is genuine, because the file and its checksum come from the same place: anyone
who could publish a release could publish a matching checksum. Signing through the SignPath
Foundation is planned; once releases are signed, the updater also refuses program files that do not
carry the expected signature.

**The local development certificate authority.** HTTPS for domain sites comes from Anvil's own
certificate authority, trusted in your user's `CurrentUser\Root` store when you click Trust. Its
private key is `data\certs\ca\ca.key`, readable only by you and administrators. Anyone who can read
that file can issue a certificate for any domain that your browsers will then trust. Settings →
Remove from Windows takes the trust away.

**PHP runs as you.** The Agent runs as your account, so a PHP script can read and change what you
can. Only run code you trust, as you would from a terminal.

**PHP pools listen on 127.0.0.1.** Apache reaches PHP over FastCGI on local TCP ports (9100 and up),
because Apache on Windows cannot use named pipes for it. Any program or account on this PC can
connect to those ports and run a PHP file you can read, as you. On a PC shared with people you do
not trust, stop Anvil when you are not using it.

**Sites open to your network have no login.** Anyone on your network can open a site or `htdocs`
folder you set to My network. Anything you serve that way needs its own login. phpMyAdmin in
particular is a database console: keep it on This PC, or give it a password.

**The name service tells your network your PC's address** for every name under your network domain,
claimed by a site or not. That is how the colleague command works.

**Revocation is not checked** when the updater verifies a signature, so updates also work offline.
A revoked but unexpired signing certificate would still pass.
