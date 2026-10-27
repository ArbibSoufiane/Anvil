using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Update;

namespace ArbiPache.Core.Tests.Update;

public sealed class UpdaterTests : IDisposable
{
    private const string Repo = "https://github.com/ArbibSoufiane/ArbiPache/releases/download/v4.1.0/";
    private static readonly string[] Exes = ["ArbiPache.exe", "ArbiPache.Agent.exe", "ArbiPache.Elevator.exe"];

    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-updater-" + Guid.NewGuid().ToString("N")));

    public UpdaterTests() => Directory.CreateDirectory(_paths.Root);

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Latest(string tag, bool draft = false, bool prerelease = false, bool zip = true, bool sums = true)
    {
        var assets = new List<string>();
        if (zip) assets.Add($$"""{"name":"ArbiPache-{{tag.TrimStart('v')}}-win64.zip","browser_download_url":"{{Repo}}ArbiPache-{{tag.TrimStart('v')}}-win64.zip"}""");
        if (sums) assets.Add($$"""{"name":"SHA256SUMS","browser_download_url":"{{Repo}}SHA256SUMS"}""");
        return $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"body":"Faster start.","assets":[{{string.Join(",", assets)}}]}""";
    }

    private static ReleaseFeed Feed(HttpStatusCode status, string body = "") =>
        new(new HttpClient(new MapHandler(new() { ["/repos/ArbibSoufiane/ArbiPache/releases/latest"] = (status, Encoding.UTF8.GetBytes(body)) })));

    [Fact]
    public async Task Newer_release_detected()
    {
        var release = await Feed(HttpStatusCode.OK, Latest("v4.1.0")).CheckAsync("4.0.0", Ct);

        Assert.Equal(new Release("4.1.0", new Uri(Repo + "ArbiPache-4.1.0-win64.zip"), new Uri(Repo + "SHA256SUMS"), "Faster start."), release);
    }

    [Theory]
    [InlineData("v4.0.0", false, false, true, true)]
    [InlineData("v3.9.0", false, false, true, true)]
    [InlineData("v4.1.0", true, false, true, true)]
    [InlineData("v4.1.0", false, true, true, true)]
    [InlineData("v4.1.0", false, false, false, true)]
    [InlineData("v4.1.0", false, false, true, false)]
    [InlineData("not-a-version", false, false, true, true)]
    public async Task Same_older_unfinished_or_incomplete_releases_are_not_offered(string tag, bool draft, bool prerelease, bool zip, bool sums)
    {
        Assert.Null(await Feed(HttpStatusCode.OK, Latest(tag, draft, prerelease, zip, sums)).CheckAsync("4.0.0", Ct));
    }

    [Fact]
    public async Task A_repository_without_releases_offers_nothing()
    {
        Assert.Null(await Feed(HttpStatusCode.NotFound).CheckAsync("4.0.0", Ct));
    }

    [Fact]
    public async Task A_rate_limited_check_is_reported()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Feed(HttpStatusCode.Forbidden, "{}").CheckAsync("4.0.0", Ct));

        Assert.Equal("GitHub refused the check for now (too many requests from this address). Try again in an hour.", e.Message);
    }

    private static byte[] ReleaseZip(string folder = "ArbiPache", params string[] extra)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in Exes.Concat(["ArbiPache.Core.dll", "catalog/components.json"]).Concat(extra))
            {
                using var w = new StreamWriter(zip.CreateEntry(folder.Length == 0 ? name : $"{folder}/{name}").Open());
                w.Write("new " + name);
            }
        return ms.ToArray();
    }

    private static string Sha(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));

    private Updater Make(byte[] zip, string? sums = null, string? publisher = null, Func<string, string, bool>? isSignedBy = null) =>
        new(_paths, new HttpClient(new MapHandler(new()
        {
            ["/ArbibSoufiane/ArbiPache/releases/download/v4.1.0/ArbiPache-4.1.0-win64.zip"] = (HttpStatusCode.OK, zip),
            ["/ArbibSoufiane/ArbiPache/releases/download/v4.1.0/SHA256SUMS"] =
                (HttpStatusCode.OK, Encoding.ASCII.GetBytes(sums ?? $"{new string('0', 64)}  ArbiPache-4.0.9-win64.zip\n{Sha(zip)} *ArbiPache-4.1.0-win64.zip\n")),
        })), publisher, isSignedBy);

    private static readonly Release Next = new("4.1.0", new Uri(Repo + "ArbiPache-4.1.0-win64.zip"), new Uri(Repo + "SHA256SUMS"), "");

    [Fact]
    public async Task Stage_unpacks_the_verified_release_beside_the_installation()
    {
        var staged = await Make(ReleaseZip()).StageAsync(Next, null, Ct);

        Assert.Equal(Path.Combine(_paths.Root, ".update", "staged-4.1.0"), Path.GetDirectoryName(staged));
        Assert.Equal("new ArbiPache.exe", File.ReadAllText(Path.Combine(staged, "ArbiPache.exe")));
        Assert.True(File.Exists(Path.Combine(staged, "catalog", "components.json")));
        Assert.True(new DirectoryInfo(Path.Combine(_paths.Root, ".update")).Attributes.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public async Task A_zip_without_a_top_folder_stages_too()
    {
        var staged = await Make(ReleaseZip(folder: "")).StageAsync(Next, null, Ct);

        Assert.Equal(Path.Combine(_paths.Root, ".update", "staged-4.1.0"), staged);
        Assert.True(File.Exists(Path.Combine(staged, "ArbiPache.Agent.exe")));
    }

    [Fact]
    public async Task Bad_checksum_refused()
    {
        var zip = ReleaseZip();

        await Assert.ThrowsAsync<ChecksumMismatchException>(() =>
            Make(zip, sums: $"{new string('a', 64)}  ArbiPache-4.1.0-win64.zip\n").StageAsync(Next, null, Ct));

        Assert.False(Directory.Exists(Path.Combine(_paths.Root, ".update", "staged-4.1.0")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_paths.Root, ".update"), "*.zip*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_sums_file_without_this_zip_is_refused()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Make(ReleaseZip(), sums: $"{new string('a', 64)}  ArbiPache-4.0.9-win64.zip\n").StageAsync(Next, null, Ct));

        Assert.Equal("The release's SHA256SUMS does not list ArbiPache-4.1.0-win64.zip, so the download cannot be checked.", e.Message);
    }

    [Fact]
    public async Task A_zip_without_the_three_programs_is_refused()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("ArbiPache/ArbiPache.exe");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Make(ms.ToArray()).StageAsync(Next, null, Ct));

        Assert.Equal("The download is not an ArbiPache release: ArbiPache.exe, ArbiPache.Agent.exe and ArbiPache.Elevator.exe must be in it.", e.Message);
        Assert.False(Directory.Exists(Path.Combine(_paths.Root, ".update", "staged-4.1.0")));
    }

    [Fact]
    public async Task Unsigned_build_refused_when_signing_required()
    {
        var checkedFiles = new List<string>();
        var updater = Make(ReleaseZip(), publisher: "Soufiane Arbib", isSignedBy: (file, publisher) =>
        {
            checkedFiles.Add(Path.GetFileName(file));
            return Path.GetFileName(file) != "ArbiPache.Agent.exe" && publisher == "Soufiane Arbib";
        });

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => updater.StageAsync(Next, null, Ct));

        Assert.Equal("ArbiPache.Agent.exe in the download is not signed by Soufiane Arbib, so the update was refused.", e.Message);
        Assert.Contains("ArbiPache.Core.dll", checkedFiles);
        Assert.False(Directory.Exists(Path.Combine(_paths.Root, ".update", "staged-4.1.0")));
    }

    [Fact]
    public async Task A_signed_build_stages()
    {
        var staged = await Make(ReleaseZip(), publisher: "Soufiane Arbib", isSignedBy: (_, _) => true).StageAsync(Next, null, Ct);

        Assert.True(File.Exists(Path.Combine(staged, "ArbiPache.exe")));
    }

    [Fact]
    public void Authenticode_check_reads_the_embedded_signature()
    {
        var signed = typeof(object).Assembly.Location;
        var unsigned = Path.Combine(_paths.Root, "unsigned.dll");
        File.Copy(typeof(UpdaterTests).Assembly.Location, unsigned);

        // The .NET runtime is signed by "CN=.NET, O=Microsoft Corporation": the check names the certificate's CN.
        Assert.True(AuthenticodeCheck.IsSignedBy(signed, ".NET"));
        Assert.False(AuthenticodeCheck.IsSignedBy(signed, "Microsoft Corporation"));
        Assert.False(AuthenticodeCheck.IsSignedBy(unsigned, ".NET"));
    }

    [Fact]
    public void A_signed_file_changed_after_signing_is_refused()
    {
        var tampered = Path.Combine(_paths.Root, "tampered.dll");
        var bytes = File.ReadAllBytes(typeof(object).Assembly.Location);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(tampered, bytes);

        Assert.False(AuthenticodeCheck.IsSignedBy(tampered, ".NET"));
    }

    private string Installed()
    {
        var target = _paths.Root;
        foreach (var name in Exes.Append("ArbiPache.Core.dll").Append("Old.Removed.dll")) File.WriteAllText(Path.Combine(target, name), "old " + name);
        Directory.CreateDirectory(Path.Combine(target, "catalog"));
        File.WriteAllText(Path.Combine(target, "catalog", "components.json"), "old catalog");
        foreach (var dir in new[] { "runtime", "data", "htdocs", "my-projects" })
        {
            Directory.CreateDirectory(Path.Combine(target, dir));
            File.WriteAllText(Path.Combine(target, dir, "keep.txt"), dir);
        }
        return target;
    }

    private string Staged(params string[] extra)
    {
        var staged = Directory.CreateDirectory(Path.Combine(_paths.Root, ".update", "staged-4.1.0", "ArbiPache")).FullName;
        foreach (var name in Exes.Append("ArbiPache.Core.dll").Concat(extra)) File.WriteAllText(Path.Combine(staged, name), "new " + name);
        Directory.CreateDirectory(Path.Combine(staged, "catalog"));
        File.WriteAllText(Path.Combine(staged, "catalog", "components.json"), "new catalog");
        return staged;
    }

    private static int GonePid()
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        return p.Id;
    }

    [Fact]
    public void Swap_keeps_runtime_data_htdocs()
    {
        var target = Installed();
        var staged = Staged("ArbiPache.New.dll");

        Updater.Swap(staged, target, GonePid());

        Assert.Equal("new ArbiPache.exe", File.ReadAllText(Path.Combine(target, "ArbiPache.exe")));
        Assert.Equal("new ArbiPache.New.dll", File.ReadAllText(Path.Combine(target, "ArbiPache.New.dll")));
        Assert.Equal("new catalog", File.ReadAllText(Path.Combine(target, "catalog", "components.json")));
        Assert.False(File.Exists(Path.Combine(target, "Old.Removed.dll")));
        foreach (var dir in new[] { "runtime", "data", "htdocs", "my-projects" })
            Assert.Equal(dir, File.ReadAllText(Path.Combine(target, dir, "keep.txt")));
        var previous = Assert.Single(Directory.GetDirectories(Path.Combine(target, ".update"), "previous-*"));
        Assert.Equal("old ArbiPache.exe", File.ReadAllText(Path.Combine(previous, "ArbiPache.exe")));
        Assert.Equal("old Old.Removed.dll", File.ReadAllText(Path.Combine(previous, "Old.Removed.dll")));
        Assert.Equal("old catalog", File.ReadAllText(Path.Combine(previous, "catalog", "components.json")));
        Assert.True(File.Exists(Path.Combine(staged, "ArbiPache.exe")));
    }

    [Fact]
    public void Swap_failure_restores_previous()
    {
        var target = Installed();
        var staged = Staged();

        // A program still holding its file: the old files stay where they are.
        using (new FileStream(Path.Combine(target, "ArbiPache.Agent.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var e = Assert.Throws<InvalidOperationException>(() => Updater.Swap(staged, target, GonePid()));
            Assert.StartsWith("ArbiPache could not replace its files", e.Message);
            Assert.EndsWith("The previous version was put back.", e.Message);
        }

        foreach (var name in Exes.Append("Old.Removed.dll")) Assert.Equal("old " + name, File.ReadAllText(Path.Combine(target, name)));
        Assert.Equal("old catalog", File.ReadAllText(Path.Combine(target, "catalog", "components.json")));
    }

    [Fact]
    public void A_copy_that_fails_halfway_puts_the_previous_version_back()
    {
        var target = Installed();
        var staged = Staged("Zz.Last.dll");

        using (new FileStream(Path.Combine(staged, "Zz.Last.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<InvalidOperationException>(() => Updater.Swap(staged, target, GonePid()));

        foreach (var name in Exes.Append("Old.Removed.dll")) Assert.Equal("old " + name, File.ReadAllText(Path.Combine(target, name)));
        Assert.False(File.Exists(Path.Combine(target, "Zz.Last.dll")));
        Assert.Equal("old catalog", File.ReadAllText(Path.Combine(target, "catalog", "components.json")));
    }

    [Fact]
    public void Swap_waits_for_the_old_window_to_close()
    {
        var target = Installed();
        var staged = Staged();

        var e = Assert.Throws<InvalidOperationException>(() =>
            Updater.Swap(staged, target, Environment.ProcessId, TimeSpan.FromMilliseconds(200)));

        Assert.Equal($"ArbiPache (process {Environment.ProcessId}) is still running, so its files cannot be replaced.", e.Message);
        Assert.Equal("old ArbiPache.exe", File.ReadAllText(Path.Combine(target, "ArbiPache.exe")));
    }

    [Fact]
    public void Clean_up_drops_staging_and_keeps_the_newest_previous_version()
    {
        var update = Path.Combine(_paths.Root, ".update");
        foreach (var dir in new[] { "staged-4.1.0", "download", "previous-4.0.0", "previous-4.0.10", "previous-4.0.9" })
            Directory.CreateDirectory(Path.Combine(update, dir));

        new Updater(_paths, new HttpClient()).CleanUp();

        Assert.Equal(["previous-4.0.10"], Directory.GetDirectories(update).Select(Path.GetFileName));
    }

    [Fact]
    public void Apply_update_arguments_are_read()
    {
        Assert.Equal(new ApplyUpdateArgs(@"D:\ArbiPache\.update\staged-4.1.0", @"D:\ArbiPache", 4242),
            ApplyUpdateArgs.Parse(["--apply-update", @"D:\ArbiPache\.update\staged-4.1.0", "--target", @"D:\ArbiPache", "--pid", "4242"]));
        Assert.Null(ApplyUpdateArgs.Parse(["--apply-update", @"D:\x", "--pid", "1"]));
        Assert.Null(ApplyUpdateArgs.Parse(["--apply-update", @"D:\x", "--target", @"D:\y", "--pid", "abc"]));
        Assert.Null(ApplyUpdateArgs.Parse([]));
    }

    private sealed class MapHandler(Dictionary<string, (HttpStatusCode Status, byte[] Body)> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsolutePath, out var file)
                ? new HttpResponseMessage(file.Status) { Content = new ByteArrayContent(file.Body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
