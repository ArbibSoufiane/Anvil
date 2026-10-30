using Anvil.Core.Components;
using Anvil.Core.Paths;

namespace Anvil.Integration.Tests.Support;

/// <summary>
/// Installs the shipped component pins into throwaway roots. Each zip is downloaded once per machine into
/// %TEMP%\anvil-it-cache and checksum-verified like any other download.
/// </summary>
internal static class Runtimes
{
    private static readonly HttpClient Http = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string CacheDir { get; } = Path.Combine(Path.GetTempPath(), "anvil-it-cache");

    public static ComponentRelease Pinned(ComponentKind kind) =>
        new ShippedCatalogProvider(new AppPaths(Repo.Root)).All().First(r => r.Kind == kind);

    /// <summary>The newest build of one PHP branch, e.g. "8.5", from the live windows.php.net feed.</summary>
    public static async Task<ComponentRelease> LatestPhpAsync(string branch, CancellationToken ct) =>
        (await new PhpCatalogProvider(Http).GetAsync(ct))
            .Where(r => r.Version.StartsWith(branch + ".", StringComparison.Ordinal))
            .OrderByDescending(r => r.Version, Comparer<string>.Create(SemVer.Compare))
            .First();

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

    /// <summary>
    /// Deletes a throwaway root. Processes the job just ended (Apache's rotatelogs, php-cgi, the database) hold their
    /// files for a moment after it closes, so this tries again for up to 10 seconds; a root still held after that fails
    /// the test rather than staying behind in %TEMP%.
    /// </summary>
    public static async Task DeleteRootAsync(AppPaths paths)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
    }

    /// <summary>
    /// Puts httpd on 127.0.0.1:<paramref name="port"/> in place of the stock Listen line Anvil commented out, so a test
    /// never listens on every interface (which would also raise a Windows Firewall prompt).
    /// </summary>
    public static void ListenOnLoopback(AppPaths paths, int port)
    {
        const string stock = "\n#Listen 80  # Anvil listens on the ports set in Settings\r";
        var conf = File.ReadAllText(paths.HttpdConf);
        if (!conf.Contains(stock, StringComparison.Ordinal)) throw new InvalidOperationException("httpd.conf has no commented stock Listen line.");
        File.WriteAllText(paths.HttpdConf, conf.Replace(stock, $"\nListen 127.0.0.1:{port}\r"));
    }
}
