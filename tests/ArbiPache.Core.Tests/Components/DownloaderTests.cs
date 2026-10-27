using System.Net;
using System.Security.Cryptography;
using System.Text;
using ArbiPache.Core.Components;

namespace ArbiPache.Core.Tests.Components;

public sealed class DownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ap-dl-" + Guid.NewGuid().ToString("N"));

    public DownloaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task Mismatch_deletes_file_and_throws()
    {
        var handler = new FakeHandler("hello");
        var dest = Path.Combine(_dir, "a.zip");

        var ex = await Assert.ThrowsAsync<ChecksumMismatchException>(() =>
            new Downloader(new HttpClient(handler)).FetchAsync(new Uri("https://example.com/a.zip"), Sha("other"), dest, null, TestContext.Current.CancellationToken));

        Assert.Equal(Sha("other"), ex.Expected);
        Assert.Equal(Sha("hello"), ex.Actual);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Http_scheme_rejected()
    {
        var handler = new FakeHandler("hello");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new Downloader(new HttpClient(handler)).FetchAsync(new Uri("http://example.com/a.zip"), Sha("hello"), Path.Combine(_dir, "a.zip"), null, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Cached_file_with_matching_hash_skips_download()
    {
        var handler = new FakeHandler("fresh");
        var dest = Path.Combine(_dir, "a.zip");
        File.WriteAllText(dest, "cached");

        var result = await new Downloader(new HttpClient(handler)).FetchAsync(new Uri("https://example.com/a.zip"), Sha("cached"), dest, null, TestContext.Current.CancellationToken);

        Assert.Equal(dest, result);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Download_writes_file_reports_progress_and_identifies_itself()
    {
        var handler = new FakeHandler("payload");
        var dest = Path.Combine(_dir, "sub", "httpd.zip");
        long reported = 0;

        await new Downloader(new HttpClient(handler)).FetchAsync(
            new Uri("https://www.apachelounge.com/download/VS18/binaries/httpd.zip"), Sha("payload").ToUpperInvariant(), dest,
            new SyncProgress(v => reported = v), TestContext.Current.CancellationToken);

        Assert.Equal("payload", File.ReadAllText(dest));
        Assert.Equal(7, reported);
        Assert.StartsWith("ArbiPache/", handler.LastRequest!.Headers.UserAgent.ToString());
        Assert.Equal("https://www.apachelounge.com/download/", handler.LastRequest.Headers.Referrer?.ToString());
    }

    [Fact]
    public async Task Referer_only_sent_to_apachelounge()
    {
        var handler = new FakeHandler("payload");

        await new Downloader(new HttpClient(handler)).FetchAsync(
            new Uri("https://windows.php.net/downloads/releases/php.zip"), Sha("payload"), Path.Combine(_dir, "php.zip"), null, TestContext.Current.CancellationToken);

        Assert.Null(handler.LastRequest!.Headers.Referrer);
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private sealed class FakeHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
