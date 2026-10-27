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

    public static async Task<string> InstallAsync(AppPaths paths, ComponentKind kind, CancellationToken ct)
    {
        var release = Pinned(kind);
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
        await new ComponentInstaller(paths, new Downloader(Http), _ => null).InstallAsync(release, null, ct);
        return release.Version;
    }

    public static AppPaths NewRoot(string name)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), $"{name} {Guid.NewGuid():N}"));
        paths.EnsureCreated();
        return paths;
    }
}
