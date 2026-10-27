using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;

namespace ArbiPache.Integration.Tests.Support;

/// <summary>
/// Installs the shipped component pins into throwaway roots. Each zip is downloaded once per machine into
/// %TEMP%\arbipache-it-cache and checksum-verified like any other download.
/// </summary>
internal static class Runtimes
{
    private static readonly HttpClient Http = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string CacheDir { get; } = Path.Combine(Path.GetTempPath(), "arbipache-it-cache");

    public static ComponentRelease Pinned(ComponentKind kind) =>
        new ShippedCatalogProvider(new AppPaths(Repo.Root)).All().First(r => r.Kind == kind);

    /// <summary>The newest build of one PHP branch, e.g. "8.5", from the live windows.php.net feed.</summary>
    public static async Task<ComponentRelease> LatestPhpAsync(string branch, CancellationToken ct) =>
        (await new PhpCatalogProvider(Http).GetAsync(ct))
            .Where(r => r.Version.StartsWith(branch + ".", StringComparison.Ordinal))
            .OrderByDescending(r => r.Version, Comparer<string>.Create(SemVer.Compare))
            .First();

    /// <summary>The newest build of one MariaDB series, e.g. "11.8", from the live downloads.mariadb.org API.</summary>
    public static async Task<ComponentRelease> LatestMariaDbAsync(string series, CancellationToken ct) =>
        (await new MariaDbCatalogProvider(Http).GetAsync(ct)).Single(r => r.Version.StartsWith(series + ".", StringComparison.Ordinal));

    public static async Task<string> InstallAsync(AppPaths paths, ComponentKind kind, CancellationToken ct) =>
        (await InstallAsync(paths, Pinned(kind), ct)).Version;

    public static async Task<InstalledComponent> InstallAsync(AppPaths paths, ComponentRelease release, CancellationToken ct)
    {
        var file = Path.GetFileName(release.Url.AbsolutePath);
        var cached = Path.Combine(CacheDir, file);
        await Gate.WaitAsync(ct);
        try
        {
            await new Downloader(Http).FetchAsync(release.Url, release.Sha256, cached, null, ct);
        }
        finally
        {
            Gate.Release();
        }

        // Seed the root's download folder so the installer finds a verified cache hit.
        var seeded = Path.Combine(paths.Tmp, "downloads", file);
        Directory.CreateDirectory(Path.GetDirectoryName(seeded)!);
        File.Copy(cached, seeded, overwrite: true);
        return await new ComponentInstaller(paths, new Downloader(Http), _ => null).InstallAsync(release, null, ct);
    }

    public static AppPaths NewRoot(string name)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), $"{name} {Guid.NewGuid():N}"));
        paths.EnsureCreated();
        return paths;
    }
}
