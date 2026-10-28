# Changelog

## 1.0.0 — unreleased

Anvil is the new name of ArbiPache 4: a rewrite of ArbiPache 3 for development on your own PC.
Settings → Import from ArbiPache 3 brings your sites over.

### Added
- Every folder in `htdocs\` answers at `http://localhost/<folder>`, and domain sites such as
  `shop.test` get their own folder and PHP version. Anvil adds their names to the hosts file.
  The Sites page names any `htdocs` folder it cannot serve because of its name, such as `my app`.
- PHP, static and proxy sites, with Laravel, Symfony, WordPress, Composer and Vite detection.
- HTTPS for domain sites from Anvil's own local certificate authority, which Windows trusts after
  one click.
- Network sharing for each site: This PC or My network. Other devices on your network open `htdocs`
  folders at `http://<this PC's address>/<folder>`.
- Network names: choose a network domain such as `arbib.pc`, and every site and folder open to the
  network also answers at `<name>.arbib.pc`. Anvil answers those name lookups itself.
- The colleague command in Settings, with a copy button: one line for Windows
  (`Add-DnsClientNrptRule`) or macOS that sends lookups for your network domain to this PC.
- Dashboard warnings when Windows treats your network as Public, with a button that opens Windows'
  network settings, and when the firewall keeps other devices out, with a button that opens it.
- Install, switch and remove Apache, PHP and MariaDB/MySQL versions from the app, checksum-verified.
  Each page offers patch updates. A new Apache that does not answer within 15 seconds, or a new PHP
  that cannot answer a request, is switched back automatically.
- PHP worker pools, one per PHP version, restarted on crash and recycled after a set number of
  requests, so every CPU core is used. Apache thread and keep-alive tuning.
- MariaDB or MySQL with a settings page showing each setting's current value, default and meaning,
  root password change, and database backups (the newest 14 kept).
- Buttons that open `httpd.conf`, `php.ini`, `my.ini` and every generated file in your editor.
- Daily log rotation for Apache and cleanup of old logs. The Logs page shows Apache, PHP, the
  database, each site, and Anvil itself.
- Check for updates in Settings: downloads the new release, verifies it against `SHA256SUMS`, swaps
  only Anvil's own program files and keeps the previous version until the new one is in place.
  Files you keep next to `Anvil.exe` stay where they are.
- Opening Anvil a second time brings the running window to the front.
- A site's network setting holds even when an app ships a `.htaccess` that allows everyone.
- HTTPS refuses host names no site claims, and Apache listens only on the ports set in Settings.
- A site folder that would publish Anvil's own files is refused, and `data\` is never served.
  Before a PHP version runs, `.php` files are refused rather than sent as their source.
- A port another program uses is refused while your sites run. When Apache cannot serve new
  settings, Anvil goes back to the previous ones and starts Apache again.
- Moving the Anvil folder re-points the database and the sites kept inside it.

### Changed
- Rewritten in C# / .NET 10 with an Avalonia desktop app. No command-line interface.
- Everything lives in the Anvil folder (`runtime\`, `data\`, `htdocs\`), so copying the folder moves
  Anvil. Only your choice of editor is kept in your user profile.
- Shipped as a plain folder of .NET files instead of a PyInstaller bundle, which is what Windows
  Defender flagged.
- mkcert is no longer downloaded: certificates are made by Anvil itself.
- Development settings (errors shown, 300 s requests, 1 GB memory, 512 MB uploads) are written into
  `php.ini` and `my.ini` once, when Anvil creates them. After that the files are yours.
- Xdebug is a normal extension: switch it on and off on the PHP page.

### Removed
- The `apachectl` command line and the terminal menu.
- pvm: Anvil installs PHP versions itself.
