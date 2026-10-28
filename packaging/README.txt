Anvil - portable
Apache, PHP and MariaDB/MySQL for Windows development, in one folder you
control.

By Soufiane Arbib - https://github.com/ArbibSoufiane/Anvil


GETTING STARTED
---------------
1. Unzip this folder somewhere only you can change: a local disk, not
   Program Files, not a network share. D:\Anvil is fine.

2. Run Anvil.exe. Opening it a second time brings the open window to the
   front instead of starting another copy.

3. Apache page -> install a version. PHP page -> install one or more PHP
   versions. Database page -> MariaDB or MySQL, if you need one. Downloads
   are checked against their published checksums before anything is used.

4. Start. Folders in htdocs\ answer at http://localhost/<folder>. Sites ->
   Add gives a folder its own domain (shop.test) or makes a reverse proxy
   to a dev server such as Vite.

Coming from ArbiPache 3? Settings -> Import from ArbiPache 3.


OPENING A SITE
--------------
On this PC:
  http://localhost/<folder>      every folder in htdocs\
  https://shop.test/             a domain site; Anvil adds it to the hosts
                                 file and its certificate authority makes
                                 HTTPS work once you click Trust

From another device on your network, after you set the site or the htdocs
folders to "My network":
  http://<your IP>/<folder>      for example http://192.168.1.5/shop
  http://<app>.<your domain>/    for example http://shop.arbib.pc/

Anvil is for development. It shows PHP errors and serves htdocs without a
login. Do not forward ports to it from your router.


NETWORK NAMES AND THE COLLEAGUE COMMAND
---------------------------------------
Settings -> Network sharing -> Network domain: pick a name that is not a
real internet domain, such as arbib.pc. While Apache runs, Anvil answers
name lookups for everything under it with this PC's address.

A colleague runs the command shown there once, with its copy button. On
Windows, in PowerShell as administrator:

  Add-DnsClientNrptRule -Namespace ".arbib.pc" -NameServers 192.168.1.5

To undo it:

  Get-DnsClientNrptRule | Where-Object Namespace -eq '.arbib.pc' | Remove-DnsClientNrptRule -Force

On macOS:

  sudo mkdir -p /etc/resolver && echo "nameserver 192.168.1.5" | sudo tee /etc/resolver/arbib.pc

and "sudo rm /etc/resolver/arbib.pc" undoes it.

The command names your PC's address, so give your PC a fixed address (a
DHCP reservation in your router). Instead of the command, a colleague can
add one hosts line per name, such as "192.168.1.5 shop.arbib.pc".


PUBLIC NETWORKS AND THE FIREWALL
--------------------------------
Windows keeps other devices out on a network it treats as Public. The
Dashboard then offers "Open network settings": choose your connection,
then Network profile type -> Private. Only do that on a network you trust.

When the firewall does not let other devices in yet, the Dashboard offers
"Open firewall". Windows asks for permission, then Anvil adds rules for
Apache's ports and its name service, for Private and Domain networks and
your local subnet only.


WHAT LIVES WHERE
----------------
Everything is inside this folder.

  Anvil.exe            the window
  Anvil.Agent.exe      runs Apache, PHP, the database and the name service
                       for the window
  Anvil.Elevator.exe   does the two things that need administrator rights:
                       the hosts file and firewall rules
  release-files.txt    the files an update replaces; anything else you
                       keep in this folder is left alone
  catalog\             the Apache, PHP and database versions on offer
  runtime\             the versions you installed (created on demand)
  data\                settings, sites, certificates, logs, backups
  htdocs\              your folders, served at http://localhost/


KEEPING IT UP TO DATE
---------------------
Settings -> Check for updates asks GitHub whether a newer release exists.
Nothing is checked until you ask.

Installing downloads the release, checks it against the SHA256SUMS file
published with it, stops your sites, swaps the program files and opens
the new version. Sites start again when "Start services when Anvil opens"
is on. The replaced files are kept in .update\previous-<version>\ and put
back if the swap fails.

Apache, PHP and the database update from their own pages. A new Apache
that does not answer within 15 seconds, or a new PHP that cannot answer a
request, is switched back automatically.


IF WINDOWS WARNS ABOUT ANVIL
----------------------------
SmartScreen may say "Windows protected your PC" for a program it has not
seen often. If you downloaded this from the GitHub page above, choose
More info -> Run anyway. If you did not, and do not trust whoever gave it
to you, delete it.

If Microsoft Defender flags a file, report it as a false positive at
https://www.microsoft.com/en-us/wdsi/filesubmission rather than turning
Defender off. Building Anvil from source is the option that requires
trusting nobody.


MOVING TO ANOTHER PC
--------------------
Stop Anvil, then copy the whole folder. Sites, settings, certificates and
installed versions all come along. Sites whose folders are outside htdocs\
need those folders at the same paths on the new PC.


LICENSE
-------
Anvil is free software under the GNU General Public License v3.0
(LICENSE.txt): you may use it, read its source, change it and share it,
and what you share stays under the same licence, with its source. The IBM
Plex fonts are under the SIL Open Font License (licenses\).
