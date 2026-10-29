using Anvil.Core.Agent;
using Anvil.Core.Components;
using Anvil.Core.Php;
using Anvil.Core.Sites;
using Serilog;

namespace Anvil.Agent;

/// <summary>
/// PHP versions the user already has, from pvm or any folder, used where they are. Each gets its own php.ini in
/// data\php like a downloaded version; Anvil never writes to, updates or deletes the folder.
/// </summary>
public sealed partial class Orchestrator
{
    private string UserProfile => userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The versions pvm has on this PC, newest first.</summary>
    public IReadOnlyList<PhpFound> FindPvmPhp()
    {
        var installed = installer.Installed(ComponentKind.Php);
        return Pvm.Find(UserProfile, phpVersionOf)
            .Select(f => new PhpFound(f.Version, f.Folder, f.Toolset, f.ThreadSafe, installed.Any(i => SameVersion(i.Version, f.Version))))
            .ToList();
    }

    /// <summary>Adds the PHP in a folder; it becomes the default when there is none.</summary>
    public async Task<ApplyResult> LinkPhpAsync(string folder, CancellationToken ct)
    {
        string version;
        try
        {
            var found = PhpFolder.Inspect(folder, phpVersionOf);
            if (InstalledPhp(found.Version) is not null) return ApplyResult.Failure($"PHP {found.Version} is already in Anvil.");
            await Locked(async () =>
            {
                await EnsureCaBundleAsync(ct);
                installer.LinkPhp(found);
                return true;
            }, ct);
            version = found.Version;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ApplyResult.Failure(e.Message);
        }

        ComponentUpdater.CarryPhpConfig(paths, version, installer.Installed(ComponentKind.Php).Select(i => i.Version));
        PhpIni().EnsureIni(version);
        if (_settings.DefaultPhp is not null) return ApplyResult.Success();
        var made = await SetDefaultPhpAsync(version, ct);
        if (!made.Ok) Log.Warning("PHP {Version} added but not made the default: {Error}", version, made.Error);
        return ApplyResult.Success();
    }

    /// <summary>Forgets a linked PHP. Its folder stays as it is; its php.ini in data\php stays for a later link.</summary>
    public Task<ApplyResult> UnlinkPhpAsync(string version, CancellationToken ct) => Locked(async () =>
    {
        if (InstalledPhp(version) is not { } entry) return ApplyResult.Failure($"PHP {version} is not in Anvil.");
        if (!entry.Linked) return ApplyResult.Failure($"PHP {version} was downloaded by Anvil, not added from a folder.");
        if (SameVersion(version, _settings.DefaultPhp))
            return ApplyResult.Failure($"PHP {version} is the default PHP. Make another version the default first.");
        if (new SiteStore(paths).All().FirstOrDefault(s => s.Type == SiteType.Php && SameVersion(version, s.PhpVersion)) is { } site)
            return ApplyResult.Failure($"{site.Name} uses PHP {version}. Give it another PHP version first.");

        installer.Remove(ComponentKind.Php, version);
        if (php.Running.Any(p => SameVersion(p.Version, version))) await StartPoolsAsync(PlanPhp(_settings), ct);
        return ApplyResult.Success();
    }, ct);
}
