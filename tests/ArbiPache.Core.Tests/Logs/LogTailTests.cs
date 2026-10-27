using System.Text;
using ArbiPache.Core.Logs;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Tests.Logs;

public sealed class LogTailTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ap-logs-" + Guid.NewGuid().ToString("N"));

    public LogTailTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // Writers such as httpd keep their log open; the tail must share it.
    private static void Append(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void ReadLast_returns_last_three_of_ten()
    {
        var path = Path.Combine(_dir, "error.log");
        File.WriteAllText(path, string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\r\n")));

        Assert.Equal(["line 8", "line 9", "line 10"], new LogTail(path).ReadLast(3));
    }

    [Fact]
    public void ReadLast_reads_across_chunks_and_while_the_writer_holds_the_file()
    {
        var path = Path.Combine(_dir, "big.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var text = string.Concat(Enumerable.Range(1, 5000).Select(i => $"[core:info] request number {i} handled fine\n"));
        writer.Write(Encoding.UTF8.GetBytes(text + "no newline yet"));
        writer.Flush();

        var last = new LogTail(path).ReadLast(1000);

        Assert.Equal(1000, last.Count);
        Assert.Equal("[core:info] request number 4002 handled fine", last[0]);
        Assert.Equal("no newline yet", last[^1]);
    }

    [Fact]
    public void ReadLast_of_missing_file_is_empty() =>
        Assert.Empty(new LogTail(Path.Combine(_dir, "none.log")).ReadLast(10));

    [Fact]
    public async Task Follow_yields_appended_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "error.log");
        File.WriteAllText(path, "old line\n");
        await using var lines = new LogTail(path, TimeSpan.FromMilliseconds(50)).FollowAsync(ct).GetAsyncEnumerator(ct);
        var next = lines.MoveNextAsync();

        Append(path, "new ");
        Append(path, "line\r\n");

        Assert.True(await next.AsTask().WaitAsync(Wait, ct));
        Assert.Equal("new line", lines.Current);
    }

    [Fact]
    public async Task Truncated_file_restarts_from_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "error.log");
        File.WriteAllText(path, "one\ntwo\nthree\n");
        await using var lines = new LogTail(path, TimeSpan.FromMilliseconds(50)).FollowAsync(ct).GetAsyncEnumerator(ct);
        var next = lines.MoveNextAsync();

        using (var truncate = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            truncate.SetLength(0);
        Append(path, "fresh\n");

        Assert.True(await next.AsTask().WaitAsync(Wait, ct));
        Assert.Equal("fresh", lines.Current);
    }

    [Fact]
    public async Task Follow_waits_for_a_file_that_does_not_exist_yet()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "later.log");
        await using var lines = new LogTail(path, TimeSpan.FromMilliseconds(50)).FollowAsync(ct).GetAsyncEnumerator(ct);
        var next = lines.MoveNextAsync();

        Append(path, "first\n");

        Assert.True(await next.AsTask().WaitAsync(Wait, ct));
        Assert.Equal("first", lines.Current);
    }

    [Fact]
    public void Discover_finds_apache_and_site_logs()
    {
        var paths = new AppPaths(_dir);
        Directory.CreateDirectory(Path.Combine(paths.Logs, "apache"));
        File.WriteAllText(Path.Combine(paths.Logs, "apache", "error.log"), "");
        Directory.CreateDirectory(Path.Combine(paths.Logs, "sites", "shop-test"));
        File.WriteAllText(Path.Combine(paths.Logs, "sites", "shop-test", "error.log"), "");
        Directory.CreateDirectory(Path.Combine(paths.Logs, "php"));
        File.WriteAllText(Path.Combine(paths.Logs, "php", "8.5.5.log"), "");
        Directory.CreateDirectory(Path.Combine(paths.Logs, "db"));
        File.WriteAllText(Path.Combine(paths.Logs, "db", "error.log"), "");
        Directory.CreateDirectory(Path.Combine(paths.Logs, "arbipache"));
        File.WriteAllText(Path.Combine(paths.Logs, "arbipache", "agent-20261026.log"), "");
        File.WriteAllText(Path.Combine(paths.Logs, "arbipache", "agent-20261027.log"), "");

        var sources = LogSources.Discover(paths);

        Assert.Equal(["apache", "php-8.5.5", "db", "site-shop-test", "arbipache"], sources.Select(s => s.Id));
        Assert.Equal(["Apache", "PHP 8.5.5", "Database", "shop-test", "ArbiPache"], sources.Select(s => s.Label));
        Assert.Equal(Path.Combine(paths.Logs, "apache", "error.log"), sources[0].Path);
        Assert.Equal(Path.Combine(paths.Logs, "arbipache", "agent-20261027.log"), sources[^1].Path);
    }

    [Fact]
    public void Discover_lists_apache_before_its_log_exists_and_prefers_newest_rotated_file()
    {
        var paths = new AppPaths(_dir);
        Directory.CreateDirectory(Path.Combine(paths.Logs, "sites", "blog"));
        File.WriteAllText(Path.Combine(paths.Logs, "sites", "blog", "error.2026-10-26.log"), "");
        File.WriteAllText(Path.Combine(paths.Logs, "sites", "blog", "error.2026-10-27.log"), "");

        var sources = LogSources.Discover(paths);

        Assert.Equal(["apache", "site-blog"], sources.Select(s => s.Id));
        Assert.Equal(Path.Combine(paths.Logs, "sites", "blog", "error.2026-10-27.log"), sources[1].Path);
    }
}
