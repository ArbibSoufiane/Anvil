using System.Text.RegularExpressions;
using Anvil.Core.Database;
using Anvil.Core.IO;
using Anvil.Core.Paths;

namespace Anvil.Core.Components;

/// <summary>
/// Installs releases side by side under runtime\, recorded in runtime\installed.json.
/// A release is unpacked into "&lt;target&gt;.staging" and renamed only when complete; a version already installed is
/// never unpacked again, so a running Apache or PHP keeps its files.
/// </summary>
public sealed partial class ComponentInstaller(AppPaths paths, Downloader dl, Func<ComponentKind, string?> activeVersion)
{
    private static readonly Lock RegistryLock = new();

    private string RegistryFile => Path.Combine(paths.Runtime, "installed.json");

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.-]{0,31}$")]
    private static partial Regex SafeVersionPattern();

    /// <summary>A version that names exactly one folder under runtime: letters, digits, dots and hyphens, never "..".</summary>
    public static bool IsSafeVersion(string v) => SafeVersionPattern().IsMatch(v) && !v.Contains("..", StringComparison.Ordinal);

    public string TargetFor(ComponentKind kind, string version, string? appliesTo = null) => kind switch
    {
        ComponentKind.Apache => paths.ApacheRuntime(version),
        ComponentKind.Php => paths.PhpRuntime(version),
        ComponentKind.MariaDb => paths.DbRuntime(DbEngine.MariaDb, version),
        ComponentKind.MySql => paths.DbRuntime(DbEngine.MySql, version),
        ComponentKind.Xdebug => Path.Combine(paths.Runtime, "xdebug", appliesTo is null ? version : $"{version}-php{appliesTo}"),
        ComponentKind.CaBundle => Path.Combine(paths.Runtime, "cabundle", version),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public async Task<InstalledComponent> InstallAsync(ComponentRelease r, IProgress<InstallProgress>? p, CancellationToken ct)
    {
        if (!IsSafeVersion(r.Version) || (r.AppliesTo is { } php && !IsSafeVersion(php)))
            throw new ArgumentException($"'{r.Version}' is not a version Anvil can install.");
        var target = TargetFor(r.Kind, r.Version, r.AppliesTo);
        lock (RegistryLock)
        {
            if (Directory.Exists(target) && Load().FirstOrDefault(e => string.Equals(e.Path, target, StringComparison.OrdinalIgnoreCase)) is { } installed)
                return installed;
        }
        var fileName = Path.GetFileName(r.Url.AbsolutePath);
        var download = Path.Combine(paths.Tmp, "downloads", fileName);

        p?.Report(new InstallProgress("Downloading", 0, null));
        var bytes = p is null ? null : new Relay(b => p.Report(new InstallProgress("Downloading", b, null)));
        await dl.FetchAsync(r.Url, r.Sha256, download, bytes, ct);

        var staging = target + ".staging";
        var aside = target + ".old";
        var movedAside = false;
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
            if (Directory.Exists(target))
            {
                // A folder installed.json does not list: a half install, or a registry lost or copied from elsewhere, so
                // it may be the version that is running. A rename fails as a whole while any file in it is open, where a
                // delete would remove everything but the files in use.
                if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
                try
                {
                    Directory.Move(target, aside);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException($"{r.Kind} {r.Version} is in use, so Anvil cannot install it again. Stop it, then try again.", e);
                }
                movedAside = true;
            }
            Directory.Move(staging, target);
        }
        catch
        {
            if (movedAside && !Directory.Exists(target)) Directory.Move(aside, target);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
        if (movedAside)
        {
            try { Directory.Delete(aside, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // the next install of this version clears it
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
