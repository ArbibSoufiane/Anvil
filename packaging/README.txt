ArbiPache - portable
Apache, PHP and MariaDB/MySQL for Windows, in one folder you control.

By Soufiane Arbib - https://github.com/ArbibSoufiane/ArbiPache


GETTING STARTED
---------------
1. Unzip this folder somewhere only you can change: a local disk, not
   Program Files, not a network share. D:\ArbiPache is fine.

2. Run ArbiPache.exe. Opening it a second time brings the open window to
   the front instead of starting another copy.

3. Apache page -> install a version. PHP page -> install one or more PHP
   versions. Database page -> MariaDB or MySQL, if you need one. Downloads
   are checked against their published checksums before anything is used.

4. Start. Folders in htdocs\ answer at http://localhost/<folder>. Sites ->
   Add gives a folder its own domain (shop.test, shop.example.com), an IP
   site served at this PC's address, or a reverse proxy.


WHAT LIVES WHERE
----------------
Everything is inside this folder.

  ArbiPache.exe            the window
  ArbiPache.Agent.exe      runs Apache, PHP and the database for the window
                           (or as a Windows service)
  ArbiPache.Elevator.exe   does the few things that need administrator
                           rights: hosts file, firewall, service install
  catalog\                 the Apache, PHP and database versions on offer
  runtime\                 the versions you installed (created on demand)
  data\                    settings, sites, certificates, logs, backups
  htdocs\                  your folders, served at http://localhost/

An update replaces only the program files. runtime\, data\ and htdocs\ are
never touched.


DEVELOPMENT AND PRODUCTION
--------------------------
The Mode page switches PHP, Apache and the database between a development
profile (errors shown, long timeouts) and a production profile (errors
logged, not shown; short timeouts and limits). It shows every setting that
changes before you switch.

In production, requests come from the internet. The Dashboard lists what
still needs attention: the database listening beyond this PC, phpMyAdmin
open to everyone, sites without HTTPS, ArbiPache not running as a service.


RUNNING AS A WINDOWS SERVICE
----------------------------
Settings -> Run as a Windows service keeps sites up after you sign out or
restart. The service runs as its own account, NT SERVICE\ArbiPacheAgent,
which can read your site folders and write only where PHP apps need to.

ArbiPache refuses to install the service while other accounts can change
files in this folder, because they could replace ArbiPache.Agent.exe and
run code as the service. A new folder on a second drive often lets every
signed-in user write to it: remove that in the folder's Properties ->
Security, or move ArbiPache to a folder under your profile.


KEEPING IT UP TO DATE
---------------------
Settings -> Check for updates asks GitHub whether a newer release exists.
Nothing is checked until you ask.

Installing downloads the release, checks it against the SHA256SUMS file
published with it, stops your sites, swaps the program files and opens
the new version. Sites start again when "Start services when ArbiPache
opens" is on. The replaced files are kept in .update\previous-<version>\
and put back if the swap fails.

Apache, PHP and the database update from their own pages. A new Apache
that does not answer within 15 seconds is switched back automatically.


IF WINDOWS WARNS ABOUT ARBIPACHE
--------------------------------
SmartScreen may say "Windows protected your PC" for a program it has not
seen often. If you downloaded this from the GitHub page above, choose
More info -> Run anyway. If you did not, and do not trust whoever gave it
to you, delete it.

If Microsoft Defender flags a file, report it as a false positive at
https://www.microsoft.com/en-us/wdsi/filesubmission rather than turning
Defender off. Building ArbiPache from source is the option that requires
trusting nobody.


MOVING TO ANOTHER PC
--------------------
Stop ArbiPache, then copy the whole folder. Sites, settings, certificates
and installed versions all come along. Sites whose folders are outside
htdocs\ need those folders at the same paths on the new PC.


LICENSE
-------
ArbiPache is licensed under the Apache License 2.0 (LICENSE.txt). The IBM
Plex fonts are under the SIL Open Font License (licenses\).
