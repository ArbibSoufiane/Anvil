# PHP from pvm and linked databases

Approved by the owner in chat on 2026-10-29, after their first round of manual testing.

## Why

The owner already runs PHP through pvm (`%USERPROFILE%\.pvm\versions\php-<ver>-...`) and MariaDB 13 and MySQL 8.0 as
Windows services on ports 3306 and 3307, each with `root` as root's password. Anvil could only use versions it
downloaded itself, and its database page could only set up its own server. They want Anvil to use what they have.

## Goals

- **PHP:** when pvm is present, the PHP page lists its versions with an Add button each. An "Add a PHP folder…" button
  links any other PHP folder. A linked version works like a downloaded one: default PHP, a site's PHP, its own worker
  pool, Xdebug.
- **Databases:** the Database page gets a "Your servers" section. Linking a server means choosing its bin folder; Anvil
  reads the engine and version, finds the Windows service that runs it and that service's settings file, and reads the
  port from it. The user enters the user name and password Anvil logs in with. Each linked server can be started,
  stopped, given another port, backed up and unlinked.
- **Root password:** any length but empty, for Anvil's own server too (done before this design).

## Non-goals

- Anvil never deletes, updates or edits the files of a linked PHP. Its own php.ini for that version lives in
  `data\php\<version>\`, as for downloaded versions; the pool's PHPRC points there.
- Anvil does not manage a linked server's databases, users or settings table. Its settings table stays for Anvil's own
  server.
- No uninstall of downloaded PHP versions (not asked for).

## Decisions

- **Start and stop of a service:** at linking, Windows asks once (UAC) and the Elevator gives the user's account the
  right to start, stop and query that one service. Starts and stops are silent after that. Unlinking takes the right back.
  Only services whose program is `mysqld.exe` or `mariadbd.exe` qualify. (Owner's choice.)
- **Port of a service:** written as `port=` in the `[mysqld]` section of the file the service's command line names with
  `--defaults-file`. That file sits in a protected folder, so Windows asks each time. A running server restarts on the
  new port. A service with no `--defaults-file` cannot change port from Anvil, and says so.
- **A bin folder with no service:** Anvil runs the server itself under its supervisor, with `--port` from Anvil's record
  and `--defaults-file` when the folder (or its parent) has a `my.ini`. It stops with Anvil's other services.
- **Credentials:** stored per linked server with DPAPI, like Anvil's own root password.
- **PHP identity:** versions stay keyed by their number. A number Anvil already has cannot be linked a second time.
  When pvm has a thread-safe and a non-thread-safe build of one version, Anvil offers the non-thread-safe one. Only
  64-bit PHP folders with `php-cgi.exe` qualify. The version comes from `php-cgi.exe`'s file version; the toolset (for
  Xdebug) from its linker version: 14.1x vc15, 14.2x vs16, 14.3x and later vs17.
- **Unlinking a PHP version** is refused while it is the default or a site uses it; its pool stops.
- **Updates:** Anvil offers no updates for linked PHP versions.

## Build order

1. Root password of any length (done).
2. PHP: folder inspection and pvm discovery; the registry records linked folders and never deletes them; every place that
   assumed `runtime\php\<version>` asks the registry; Agent operations to list candidates, link and unlink; PHP page.
3. Databases: bin folder inspection, service lookup, `my.ini` port read and write; linked server store and secrets;
   Elevator verbs `db-service-access` and `db-port` with validation; Agent operations (inspect, link, unlink, start,
   stop, set port, back up); results carry the elevation they still need and the app runs it, then retries once;
   Database page section.
4. Rebuild, deploy to the owner's test folder, check in the running app, merge everything into main.
