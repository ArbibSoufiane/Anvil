using Anvil.Core.Database;
using Anvil.Core.IO;
using Anvil.Core.Paths;

namespace Anvil.Core.Components;

/// <summary>
/// Installs releases side by side under runtime\, recorded in runtime\installed.json.
/// A release is unpacked into "&lt;target&gt;.staging" and renamed only when complete.
/// </summary>
public sealed class ComponentInstaller(AppPaths paths, Downloader dl, Func<ComponentKind, string?> activeVersion)
{
    private static readonly Lock RegistryLock = new();

    private string RegistryFile => Path.Combine(paths.Runtime, "installed.json");

    public string TargetFor(ComponentKind kind, string version, string? appliesTo = null) => kind switch
    {
        ComponentKind.Apache => paths.ApacheRuntime(version),
        ComponentKind.Php => paths.PhpRuntime(version),
        ComponentKind.MariaDb => paths.DbRuntime(DbEngine.MariaDb, version),
        ComponentKind.MySql => paths.DbRuntime(DbEngine.MySql, version),
        ComponentKind.Xdebug => Path.Combine(paths.Runtime, "xdebug", appliesTo is null ? version : $"{version}-php{appliesTo}"),
        ComponentKind.CaBundle => Path.Combine(paths.Runtime, "cabundle", version),
        ComponentKind.ModSecurity => Path.Combine(paths.Runtime, "modsecurity", version),
        ComponentKind.CoreRuleSet => Path.Combine(paths.Runtime, "crs", version),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public async Task<InstalledComponent> InstallAsync(ComponentRelease r, IProgress<InstallProgress>? p, CancellationToken ct)
    {
        var target = TargetFor(r.Kind, r.Version, r.AppliesTo);
        var fileName = Path.GetFileName(r.Url.AbsolutePath);
        var download = Path.Combine(paths.Tmp, "downloads", fileName);

        p?.Report(new InstallProgress("Downloading", 0, null));
        var bytes = p is null ? null : new Relay(b => p.Report(new InstallProgress("Downloading", b, null)));
        await dl.FetchAsync(r.Url, r.Sha256, download, bytes, ct);

        var staging = target + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        try
        {
            p?.Report(new InstallProgress("Extracting", 0, null));
            if (IsSingleFile(fileName))
            {
                Directory.CreateDirectory(staging);
                File.Copy(download, Path.Combine(staging, r.Kind == ComponentKind.CaBundle ? "cacert.pem" : fileName));
            }
            else
            {
                ZipExtractor.Extract(download, staging, r.StripRoot);
            }
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }

        var entry = new InstalledComponent(r.Kind, r.Version, target, r.Sha256.ToLowerInvariant(), DateTimeOffset.UtcNow, r.Toolset, r.AppliesTo);
        lock (RegistryLock)
        {
            var all = Load();
            all.RemoveAll(e => string.Equals(e.Path, target, StringComparison.OrdinalIgnoreCase));
            all.Add(entry);
            JsonFiles.Write(RegistryFile, all);
        }
        p?.Report(new InstallProgress("Installed", 0, null));
        return entry;
    }

    public IReadOnlyList<InstalledComponent> Installed(ComponentKind kind)
    {
        lock (RegistryLock)
            return Load()
                .Where(e => e.Kind == kind)
                .OrderByDescending(e => e.Version, Comparer<string>.Create(SemVer.Compare))
                .ToList();
    }

    public void Remove(ComponentKind kind, string version)
    {
        if (string.Equals(activeVersion(kind), version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{kind} {version} is in use. Switch to another version before removing it.");

        lock (RegistryLock)
        {
            var all = Load();
            foreach (var entry in all.Where(e => e.Kind == kind && string.Equals(e.Version, version, StringComparison.OrdinalIgnoreCase)))
                if (Directory.Exists(entry.Path)) Directory.Delete(entry.Path, recursive: true);
            all.RemoveAll(e => e.Kind == kind && string.Equals(e.Version, version, StringComparison.OrdinalIgnoreCase));
            JsonFiles.Write(RegistryFile, all);
        }
    }

    private List<InstalledComponent> Load() =>
        File.Exists(RegistryFile) ? JsonFiles.Read<List<InstalledComponent>>(RegistryFile) ?? [] : [];

    private static bool IsSingleFile(string fileName) =>
        fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase);

    private sealed class Relay(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
