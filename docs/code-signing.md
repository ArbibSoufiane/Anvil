# Signing Anvil releases with SignPath Foundation

Nothing in this guide is switched on yet. Releases stay unsigned until you have tested a build
yourself, applied, and been accepted. Follow it in order.

**Releases are built locally now.** `build\release.ps1` builds on your PC and only uploads the
result; there are no GitHub Actions. SignPath Foundation signs only files it has seen built by
GitHub Actions on GitHub-hosted runners, so applying means bringing back a release workflow first.
Where this guide says `release.yml`, that workflow does not exist yet: it ran `build/publish.ps1` on
`windows-latest` for a `v<version>` tag and created the release with `gh release create`.

Sources, read on 2026-10-28:

- SignPath Foundation, *Conditions for Open Source projects*: <https://signpath.org/terms>
- SignPath Foundation home and application form: <https://signpath.org/>, <https://signpath.org/apply>
- SignPath documentation, *Trusted Build Systems: GitHub*:
  <https://docs.signpath.io/trusted-build-systems/github>
- SignPath documentation, *Artifact configuration reference* and *examples*:
  <https://docs.signpath.io/artifact-configuration/reference>,
  <https://docs.signpath.io/artifact-configuration/examples>
- The submit action: <https://github.com/SignPath/github-action-submit-signing-request>

Their terms change; read them again before you apply.

## 1. Why sign

Windows judges an unsigned `.exe` by how often it has seen that exact file. Every Anvil release is a
new file, so SmartScreen says "Windows protected your PC" and Defender's heuristics look harder at a
program that edits the hosts file, opens firewall ports and downloads executables. A signature ties
every release to one publisher whose reputation carries over from release to release, and lets the
in-app updater refuse program files that were not signed by that publisher.

With SignPath Foundation the certificate is issued to **SignPath Foundation**, not to you: Windows
shows "SignPath Foundation" as the publisher. In return SignPath checks, for every release, that the
files were built by GitHub Actions from the source in your repository. You need no company and pay
nothing.

## 2. Eligibility, and how Anvil meets it

| Condition (signpath.org/terms) | Anvil |
|---|---|
| **No malware or potentially unwanted programs.** | Anvil changes the system only when you ask: hosts entries and firewall rules through UAC, the certificate authority through Windows' own prompt. |
| **OSI-approved licence for all components, no commercial dual-licensing.** | GPL-3.0 (`LICENSE`). Shipped packages are MIT, BSD or Apache-2.0; the IBM Plex fonts are OFL-1.1, also OSI-approved. Apache, PHP and the databases are downloaded at run time, not shipped. |
| **No proprietary code.** System libraries are allowed. | None. The self-contained .NET runtime is MIT. |
| **Actively maintained.** | Commits and releases on `ArbibSoufiane/Anvil`. |
| **Already released** in the form that should be signed. | Publish at least one unsigned release first (section 3). |
| **Documented** on the download page. | `README.md` and the release notes. |
| **Sign your own projects and binaries only.** | Sign only `Anvil.exe`, `Anvil.Agent.exe`, `Anvil.Elevator.exe` and `Anvil.*.dll`. Every other DLL and `createdump.exe` come from Microsoft or other projects and ship unsigned by you (the terms allow that inside a package). |
| **No hacking tools.** Nothing that scans for exploitable weaknesses such as unprotected ports. | The Dashboard warns only about Anvil's own setup (its firewall rules, the network profile it needs). Say so in the application so the reviewer does not read it as a port scanner. |
| **Announce system changes; provide uninstallation.** | Anvil asks before every change. Add the "Removing Anvil" section from section 5 to `README.md` before applying. |
| **Multi-factor authentication** for SignPath and GitHub for every team member. | Turn on 2FA for your GitHub account, and use it on SignPath when you get access. |
| **Roles:** authors, reviewers (every outside pull request reviewed), approvers (every signing request approved by hand). | You hold all three roles. Say so in the policy text (section 5). |
| **Code signing policy** on the home page and the release pages. | Section 5. |
| **Artifact configuration** restricts product name and version of every signed file. | Section 6: product name `Anvil`, product version from `Directory.Build.props`. |
| **Builds verifiable from source**; for open source projects every job on GitHub-hosted runners. | `release.yml` runs on `windows-latest`. |

SignPath also says it needs "a certain verifiable reputation" for downloadable programs, and that it
may refuse any project without arbitration. Mention ArbiPache 3 and its history in the application.

## 3. Before applying

1. Create the public repository `github.com/ArbibSoufiane/Anvil` and push `main`.
2. Turn on two-factor authentication on your GitHub account.
3. Protect `main`: Settings → Rules → a ruleset that blocks force pushes and requires pull requests
   for changes from anyone but you. SignPath can check these rules later (section 6).
4. Test a build on your own PC: `./build/publish.ps1 -Trial`, then run `dist\Anvil\Anvil.exe` and
   work through the manual checklist.
5. Date the `CHANGELOG.md` heading (`## 1.0.0 — 2026-11-..`), commit, tag `v1.0.0` and push the tag.
   `release.yml` publishes the unsigned release. This is the "already released" condition.
6. Add the README sections from section 5 and push them, so the reviewer sees them.

## 4. Applying

1. Open <https://signpath.org/apply> and fill in the form: project name Anvil, repository URL,
   homepage (the repository), licence GPL-3.0, the download page (Releases), and a short description:
   "A desktop app that runs Apache, PHP and MariaDB/MySQL for web development on Windows, from one
   portable folder."
2. Describe the build: GitHub Actions, `windows-latest`, `build/publish.ps1`, one ZIP with three
   programs. Mention that the Elevator asks for administrator rights only to edit the hosts file and
   Anvil's firewall rules.
3. Wait for the answer. Once accepted, SignPath creates an organization for you on SignPath.io and
   tells you its **organization ID**.
4. On SignPath.io:
   - create a **project** (slug `anvil`) linked to the predefined Trusted Build System **GitHub.com**;
   - install the [SignPath GitHub App](https://github.com/apps/signpath) on the Anvil repository;
   - add the **artifact configuration** from section 6;
   - use the **signing policy** SignPath sets up for open source projects (slug usually
     `release-signing`), with yourself as approver;
   - create a CI user with submitter permission and copy its **API token**.
5. In the GitHub repository, Settings → Secrets and variables → Actions: add the secret
   `SIGNPATH_API_TOKEN` and the variable `SIGNPATH_ORGANIZATION_ID`.

## 5. Text for README.md

Add these sections near the end of `README.md`, before "License". The terms require the heading
"Code signing policy" on the home page and the release pages; add one line linking to it in the
release notes too.

```markdown
## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

- Committers and reviewers: [Soufiane Arbib](https://github.com/ArbibSoufiane)
- Approvers: [Soufiane Arbib](https://github.com/ArbibSoufiane)

Every release is built by GitHub Actions from this repository and approved by hand before it is
signed. Only Anvil's own programs (`Anvil.exe`, `Anvil.Agent.exe`, `Anvil.Elevator.exe`,
`Anvil.*.dll`) carry the signature; the .NET runtime files in the same folder are Microsoft's.

## Privacy

This program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it. Anvil goes on the network only when
you ask it to: Settings → Check for updates asks GitHub for the latest release, and installing
Apache, PHP, MariaDB, MySQL, Xdebug or the CA bundle downloads them from the address listed in
`catalog\components.json` or from the project that publishes them (php.net, mariadb.org). Those
sites see your IP address and Anvil's version in the request, under their own privacy policies.
On your local network, Anvil answers name lookups for your network domain and serves the sites
you open to it.

## Removing Anvil

1. Settings → Certificates and updates → Local certificate authority → **Remove from Windows**.
2. Delete your domain sites: when the last one goes, Anvil removes its block from the hosts file
   (Windows asks for permission). Or remove the lines between `# >>> Anvil >>>` and `# <<< Anvil <<<` in
   `C:\Windows\System32\drivers\etc\hosts` yourself, as administrator.
3. Windows Defender Firewall → Advanced settings → Inbound Rules: delete **Anvil web server** and
   **Anvil name service**.
4. Settings → turn off **Start Anvil with Windows**, quit Anvil from the tray, and delete the Anvil folder.
   Your choice of editor is in `%LOCALAPPDATA%\Anvil\editor.txt`; delete that folder too.
5. Colleagues who ran the colleague command undo it with the command shown under "Network names".
```

## 6. The release workflow, signed

**Not enabled.** Add this only after SignPath has accepted Anvil and section 4 is done.

`build/publish.ps1` does everything in one go today: test, publish into `dist\Anvil`, write
`release-files.txt`, zip, checksum. The signature has to go on between "publish" and "zip", so the
script needs a switch that stops after `dist\Anvil` is complete (say `-NoZip`) and a second, small
script that zips `dist\Anvil` and writes `SHA256SUMS` and `RELEASE_NOTES.md` (say
`build/package.ps1`). Move the zip and checksum code there unchanged; names must stay as
`ReleaseFeed.ZipName` and `ReleaseFeed.SumsName` expect.

Artifact configuration (SignPath.io → project → Artifact configurations). The upload step below
zips `dist\Anvil`, so the root element is `<zip-file>`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <parameters>
    <parameter name="version" required="true" />
  </parameters>
  <zip-file>
    <pe-file-set product-name="Anvil" product-version="${version}">
      <include path="Anvil.exe" />
      <include path="Anvil.Agent.exe" />
      <include path="Anvil.Elevator.exe" />
      <include path="Anvil.*.dll" min-matches="1" max-matches="unbounded" />
      <for-each>
        <authenticode-sign />
      </for-each>
    </pe-file-set>
  </zip-file>
</artifact-configuration>
```

Anvil's assemblies already carry `Product` = `Anvil` and a product version equal to `<Version>`
(`IncludeSourceRevisionInInformationalVersion` is off), so the restriction holds for every build.

`release.yml`, replacing the single "Build" step:

```yaml
permissions:
  contents: write
  actions: read

      - name: Build
        shell: pwsh
        run: ./build/publish.ps1 -NoZip

      - name: Read the version
        id: version
        shell: pwsh
        run: |
          $v = ([xml](Get-Content Directory.Build.props -Raw)).SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
          "value=$v" >> $env:GITHUB_OUTPUT

      - name: Upload the unsigned folder
        id: unsigned
        uses: actions/upload-artifact@v7
        with:
          name: anvil-unsigned
          path: dist/Anvil/

      - name: Sign with SignPath
        uses: signpath/github-action-submit-signing-request@v3
        with:
          api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
          organization-id: ${{ vars.SIGNPATH_ORGANIZATION_ID }}
          project-slug: anvil
          signing-policy-slug: release-signing
          github-artifact-id: ${{ steps.unsigned.outputs.artifact-id }}
          wait-for-completion: true
          wait-for-completion-timeout-in-seconds: 3600
          output-artifact-directory: dist/Anvil-signed
          parameters: |
            version: ${{ toJSON(steps.version.outputs.value) }}

      - name: Put the signed files in place
        shell: pwsh
        run: |
          Remove-Item dist/Anvil -Recurse -Force
          Move-Item dist/Anvil-signed dist/Anvil
          foreach ($exe in 'Anvil.exe', 'Anvil.Agent.exe', 'Anvil.Elevator.exe') {
            $s = Get-AuthenticodeSignature "dist/Anvil/$exe"
            if ($s.Status -ne 'Valid' -or $s.SignerCertificate.GetNameInfo('SimpleName', $false) -ne 'SignPath Foundation') {
              throw "$exe is not signed by SignPath Foundation ($($s.Status))."
            }
          }

      - name: Zip and checksum
        shell: pwsh
        run: ./build/package.ps1
```

The signing request waits for your manual approval on SignPath.io, so the job sits at "Sign with
SignPath" until you approve; the timeout above gives you an hour. The "Publish on GitHub" step stays
as it is.

Once this works, SignPath can also enforce rules on the build and the repository (pipeline policies:
`github-build-policies` with `runners: require_github_hosted: true`, and `github-scm-policies` for the
`main` ruleset). See the GitHub page of their documentation.

## 7. Pinning the publisher in the updater

`Updater` already takes `requiredPublisher`: when it is set, `StageAsync` refuses a downloaded
release whose program files are not signed by that name, verified by WinVerifyTrust. It is not set
anywhere yet. Set it only after the first signed release is out, so the copy that pins it can update
to a signed release.

**Fix the file filter first.** `Updater.IsOurs` treats every `.exe` in the release folder as Anvil's,
and a self-contained .NET publish ships Microsoft's `createdump.exe`, signed by Microsoft, not
SignPath Foundation. Pinned as it is, every update would be refused. Narrow `IsOurs` to the files
the artifact configuration signs (`Anvil*.exe`, `Anvil*.dll`), with a test in `UpdaterTests` that
stages a release containing an unsigned or differently signed `createdump.exe` and expects it to
pass.

Then pass the publisher where the window builds its updaters, `App.axaml.cs` and
`AppUpdate.FromGitHub`:

```csharp
new Updater(paths, http, requiredPublisher: "SignPath Foundation")
```

`AuthenticodeCheck.IsSignedBy` compares the certificate's simple name (its CN), so the value is
exactly the name Windows shows as publisher. Release it as a minor version with a CHANGELOG line:
"Updates are refused unless Anvil's programs are signed by SignPath Foundation."

## 8. Checking a signed build

On the unzipped release, before you publish it:

```powershell
Get-ChildItem dist\Anvil\Anvil*.exe, dist\Anvil\Anvil*.dll |
    Get-AuthenticodeSignature |
    Format-Table Path, Status, @{ n = 'Signer'; e = { $_.SignerCertificate.Subject } } -AutoSize
```

Every row should say `Valid` and `CN=SignPath Foundation, ...`. With the Windows SDK installed:

```powershell
signtool verify /pa /v dist\Anvil\Anvil.exe
```

`/pa` uses the Authenticode policy; `/v` prints the chain and the timestamp. A missing timestamp
means the signature dies with the certificate; SignPath timestamps by default.

## 9. Scanning before you publish

Defender, with the latest definitions:

```powershell
& "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -SignatureUpdate
& "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -Scan -ScanType 3 -File "$PWD\dist\Anvil"
```

Then upload `dist\Anvil-<version>-win64.zip` to <https://www.virustotal.com>. A few heuristic hits
from small engines are common for a new file; a hit from Microsoft, or several from major engines,
is a reason to stop and report a false positive before you publish
(<https://www.microsoft.com/en-us/wdsi/filesubmission>).
