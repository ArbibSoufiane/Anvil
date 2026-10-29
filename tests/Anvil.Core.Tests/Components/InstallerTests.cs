using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Anvil.Core.Components;
using Anvil.Core.Paths;
using Anvil.Core.Php;

namespace Anvil.Core.Tests.Components;

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

    private string LinkedFolder()
    {
        var folder = Path.Combine(_paths.Root, "pvm", "php-8.4.23");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "php-cgi.exe"), "MZ");
        return folder;
    }

    [Fact]
    public void A_linked_php_is_listed_with_its_own_folder()
    {
        var folder = LinkedFolder();

        Installer([]).LinkPhp(new PhpFolderInfo(folder, "8.4.23", "vs17", ThreadSafe: true));

        var entry = Assert.Single(Installer([]).Installed(ComponentKind.Php));
        Assert.Equal(("8.4.23", folder, "vs17", true, true), (entry.Version, entry.Path, entry.Toolset, entry.Linked, entry.ThreadSafe));
    }

    [Fact]
    public void Removing_a_linked_php_keeps_its_folder()
    {
        var folder = LinkedFolder();
        var installer = Installer([]);
        installer.LinkPhp(new PhpFolderInfo(folder, "8.4.23", "vs17", ThreadSafe: false));

        installer.Remove(ComponentKind.Php, "8.4.23");

        Assert.Empty(installer.Installed(ComponentKind.Php));
        Assert.True(File.Exists(Path.Combine(folder, "php-cgi.exe")));
    }

    [Fact]
    public async Task A_php_version_is_in_Anvil_once()
    {
        var body = Zip("php-cgi.exe");
        var installer = Installer(body);
        await installer.InstallAsync(Release(ComponentKind.Php, "8.4.23", "php.zip", body), null, TestContext.Current.CancellationToken);

        var e = Assert.Throws<InvalidOperationException>(() => installer.LinkPhp(new PhpFolderInfo(LinkedFolder(), "8.4.23", "vs17", ThreadSafe: false)));
        Assert.Equal("PHP 8.4.23 is already in Anvil.", e.Message);
    }

    [Fact]
    public async Task Downloading_a_php_version_that_is_linked_is_refused()
    {
        var body = Zip("php-cgi.exe");
        var installer = Installer(body);
        installer.LinkPhp(new PhpFolderInfo(LinkedFolder(), "8.4.23", "vs17", ThreadSafe: false));

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(Release(ComponentKind.Php, "8.4.23", "php.zip", body), null, TestContext.Current.CancellationToken));
        Assert.Equal($"PHP 8.4.23 is already in Anvil, from {LinkedFolder()}.", e.Message);
        Assert.False(Directory.Exists(_paths.PhpRuntime("8.4.23")));
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

    [Theory]
    [InlineData("2.4.69", true)]
    [InlineData("2026-10-20", true)]
    [InlineData("8.5", true)]
    [InlineData(@"..\x", false)]
    [InlineData("a/b", false)]
    [InlineData("1..2", false)]
    [InlineData("-1", false)]
    [InlineData("", false)]
    public void Version_must_be_safe(string v, bool ok) => Assert.Equal(ok, ComponentInstaller.IsSafeVersion(v));

    [Fact]
    public async Task Unsafe_version_is_refused_before_anything_is_written()
    {
        var body = Zip("x.txt");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Installer(body).InstallAsync(Release(ComponentKind.Apache, @"..\..\evil", "httpd.zip", body), null, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(Path.Combine(_paths.Runtime, "evil")));
        Assert.False(Directory.Exists(Path.Combine(_paths.Root, "evil")));
    }

    [Fact]
    public async Task Reinstalling_an_installed_version_keeps_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = Zip("Apache24/bin/httpd.exe");
        var release = Release(ComponentKind.Apache, "2.4.69", "httpd.zip", body, "Apache24");
        var first = await Installer(body).InstallAsync(release, null, ct);
        var marker = Path.Combine(first.Path, "keep.txt");
        File.WriteAllText(marker, "x");

        var again = await Installer([]).InstallAsync(release, null, ct); // nothing to download: a fetch would fail the hash

        Assert.True(File.Exists(marker));
        Assert.Equal(first.Path, again.Path);
    }

    [Fact]
    public async Task A_folder_the_registry_does_not_list_is_installed_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = Zip("Apache24/bin/httpd.exe");
        Directory.CreateDirectory(_paths.ApacheRuntime("2.4.69"));
        File.WriteAllText(Path.Combine(_paths.ApacheRuntime("2.4.69"), "half.txt"), "x");

        var installed = await Installer(body).InstallAsync(Release(ComponentKind.Apache, "2.4.69", "httpd.zip", body, "Apache24"), null, ct);

        Assert.True(File.Exists(Path.Combine(installed.Path, "bin", "httpd.exe")));
        Assert.False(File.Exists(Path.Combine(installed.Path, "half.txt")));
        Assert.False(Directory.Exists(installed.Path + ".old"));
    }

    [Fact]
    public async Task An_unlisted_folder_in_use_is_left_whole()
    {
        // installed.json lost or copied from elsewhere while this version runs: its httpd.exe is open.
        var ct = TestContext.Current.CancellationToken;
        var body = Zip("Apache24/bin/httpd.exe");
        var bin = Path.Combine(_paths.ApacheRuntime("2.4.69"), "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "apr.dll"), "x");
        using (new FileStream(Path.Combine(bin, "httpd.exe"), FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Installer(body).InstallAsync(Release(ComponentKind.Apache, "2.4.69", "httpd.zip", body, "Apache24"), null, ct));

            Assert.Contains("in use", e.Message);
        }

        Assert.True(File.Exists(Path.Combine(bin, "apr.dll")));
        Assert.True(File.Exists(Path.Combine(bin, "httpd.exe")));
        Assert.False(Directory.Exists(_paths.ApacheRuntime("2.4.69") + ".staging"));
        Assert.Empty(Installer(body).Installed(ComponentKind.Apache));
    }
}
