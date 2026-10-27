using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Tests.Components;

public sealed class InstallerTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-inst-" + Guid.NewGuid().ToString("N")));
    private string? _active;

    public InstallerTests() => _paths.EnsureCreated();

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private static byte[] Zip(params string[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(name);
            }
        return ms.ToArray();
    }

    private ComponentInstaller Installer(byte[] body) =>
        new(_paths, new Downloader(new HttpClient(new BytesHandler(body))), _ => _active);

    private static ComponentRelease Release(ComponentKind kind, string version, string file, byte[] body, string? stripRoot = null) =>
        new(kind, version, new Uri($"https://example.com/{file}"), Convert.ToHexStringLower(SHA256.HashData(body)), StripRoot: stripRoot);

    [Fact]
    public async Task Install_creates_folder_and_registry_entry()
    {
        var body = Zip("Apache24/bin/httpd.exe", "Apache24/conf/httpd.conf");
        var release = Release(ComponentKind.Apache, "2.4.69", "httpd.zip", body, "Apache24") with { Toolset = "VS18" };

        var installed = await Installer(body).InstallAsync(release, null, TestContext.Current.CancellationToken);

        Assert.Equal(_paths.ApacheRuntime("2.4.69"), installed.Path);
        Assert.True(File.Exists(Path.Combine(installed.Path, "bin", "httpd.exe")));
        var entry = Assert.Single(Installer(body).Installed(ComponentKind.Apache));
        Assert.Equal("2.4.69", entry.Version);
        Assert.Equal(release.Sha256, entry.Sha256);
        Assert.Equal("VS18", entry.Toolset);
        Assert.True(File.Exists(Path.Combine(_paths.Runtime, "installed.json")));
    }

    [Fact]
    public async Task Xdebug_entry_records_its_php_minor()
    {
        var body = Encoding.ASCII.GetBytes("MZ");
        var release = Release(ComponentKind.Xdebug, "3.5.3", "php_xdebug-3.5.3-8.5-nts-vs17-x86_64.dll", body) with { AppliesTo = "8.5" };

        await Installer(body).InstallAsync(release, null, TestContext.Current.CancellationToken);

        var entry = Assert.Single(Installer(body).Installed(ComponentKind.Xdebug));
        Assert.Equal("8.5", entry.AppliesTo);
        Assert.True(File.Exists(Path.Combine(entry.Path, "php_xdebug-3.5.3-8.5-nts-vs17-x86_64.dll")));
    }

    [Fact]
    public async Task Failed_extract_leaves_no_folder_and_no_entry()
    {
        var body = Zip("Apache24/bin/httpd.exe", "Apache24/../../evil.txt");
        var release = Release(ComponentKind.Apache, "2.4.69", "httpd.zip", body, "Apache24");

        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(body).InstallAsync(release, null, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(_paths.ApacheRuntime("2.4.69")));
        Assert.False(Directory.Exists(_paths.ApacheRuntime("2.4.69") + ".staging"));
        Assert.Empty(Installer(body).Installed(ComponentKind.Apache));
    }

    [Fact]
    public async Task Remove_active_version_throws()
    {
        var body = Zip("Apache24/bin/httpd.exe");
        var installer = Installer(body);
        await installer.InstallAsync(Release(ComponentKind.Apache, "2.4.69", "httpd.zip", body, "Apache24"), null, TestContext.Current.CancellationToken);
        _active = "2.4.69";

        Assert.Throws<InvalidOperationException>(() => installer.Remove(ComponentKind.Apache, "2.4.69"));
        Assert.True(Directory.Exists(_paths.ApacheRuntime("2.4.69")));
    }

    [Fact]
    public async Task Remove_inactive_deletes_folder_and_entry()
    {
        var body = Zip("Apache24/bin/httpd.exe");
        var installer = Installer(body);
        await installer.InstallAsync(Release(ComponentKind.Apache, "2.4.69", "httpd.zip", body, "Apache24"), null, TestContext.Current.CancellationToken);
        _active = "2.4.70";

        installer.Remove(ComponentKind.Apache, "2.4.69");

        Assert.False(Directory.Exists(_paths.ApacheRuntime("2.4.69")));
        Assert.Empty(installer.Installed(ComponentKind.Apache));
    }

    [Fact]
    public async Task Pem_release_is_copied_not_extracted()
    {
        var body = Encoding.ASCII.GetBytes("-----BEGIN CERTIFICATE-----");
        var release = Release(ComponentKind.CaBundle, "2026-10-20", "cacert-2026-10-20.pem", body);

        var installed = await Installer(body).InstallAsync(release, null, TestContext.Current.CancellationToken);

        Assert.Equal("-----BEGIN CERTIFICATE-----", File.ReadAllText(Path.Combine(installed.Path, "cacert.pem")));
    }

    [Fact]
    public async Task Progress_reports_download_and_extract_stages()
    {
        var body = Zip("Apache24/bin/httpd.exe");
        var stages = new List<string>();

        await Installer(body).InstallAsync(Release(ComponentKind.Apache, "2.4.69", "httpd.zip", body, "Apache24"),
            new SyncProgress(p => stages.Add(p.Stage)), TestContext.Current.CancellationToken);

        Assert.Contains("Downloading", stages);
        Assert.Contains("Extracting", stages);
    }

    private sealed class SyncProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }

    private sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
