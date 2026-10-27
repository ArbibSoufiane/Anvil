# Changelog

## 4.0.0 — unreleased

ArbiPache 4 is a new program: a rewrite of ArbiPache 3 in C# / .NET 10 with an Avalonia window.
Settings → Import from ArbiPache 3 brings your sites over.

### Added
- Three kinds of site: domain sites (`shop.test`, `shop.example.com`), IP sites served at this PC's
  address with extra folders such as `/demo`, and every `htdocs` folder at `http://localhost/<folder>`
  and, if you allow it, at this PC's network address.
- PHP, static and proxy sites, with Laravel, Symfony, WordPress, Composer and Vite detection.
- Per-site access: everyone, my network, this PC or specific addresses, with an optional password.
- Per-site HTTPS: Let's Encrypt with automatic renewal, a certificate a client sent you (`.pfx` or
  `.crt` + `.key`), HTTP behind a client's load balancer, or a local development certificate from
  ArbiPache's own certificate authority.
- Development and Production modes that rewrite PHP, Apache and database limits. The Mode page shows
  every change before you switch, backs up every touched file and restores it if anything fails.
- Production warnings on the Dashboard: the database listening beyond this PC, phpMyAdmin open to
  everyone, PHP errors shown, sites without HTTPS, ArbiPache not running as a service.
- Install, switch and remove Apache, PHP and MariaDB/MySQL versions from the app, checksum-verified.
  Patch updates are offered on each page; a new Apache that does not answer within 15 seconds is
  switched back automatically.
- PHP worker pools, one per PHP version, restarted on crash and recycled after a set number of
  requests, so every CPU core is used. Apache thread and keep-alive tuning.
- MariaDB or MySQL with a settings page showing each setting's current value, default and meaning,
  root password change, and database backups (the newest 14 kept).
- Buttons that open `httpd.conf`, `php.ini`, `my.ini` and every generated file in your editor.
- Run as a Windows service, so sites stay up after sign-out and restarts.
- Daily log rotation for Apache and cleanup of old logs.
- Check for updates in Settings: downloads the new release, verifies it against `SHA256SUMS`, swaps
  the program files and keeps the previous version until the new one is in place.
- Opening ArbiPache a second time brings the running window to the front.

### Changed
- Rewritten in C# / .NET 10 with an Avalonia desktop app. No command-line interface.
- Everything lives in the ArbiPache folder (`runtime\`, `data\`, `htdocs\`); nothing is kept in your
  user profile, so copying the folder moves ArbiPache.
- Shipped as a plain folder of .NET files instead of a PyInstaller bundle, which is what
  Windows Defender flagged.
- mkcert is no longer downloaded: certificates are made by ArbiPache itself.

### Removed
- The `apachectl` command line and the terminal menu.
- pvm: ArbiPache installs PHP versions itself.
