using ArbiPache.Core.IO;

namespace ArbiPache.Core.Tests.IO;

public sealed class RetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 4, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("ap-retention-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private FileInfo Log(string name, TimeSpan age)
    {
        var file = Path.Combine(_dir, name);
        File.WriteAllText(file, "");
        File.SetLastWriteTimeUtc(file, (Now - age).UtcDateTime);
        return new FileInfo(file);
    }

    [Fact]
    public void Logs_older_than_14_days_expired()
    {
        FileInfo[] files =
        [
            Log("error.2026-10-12.log", TimeSpan.FromDays(15)),
            Log("error.2026-10-13.log", TimeSpan.FromDays(14) + TimeSpan.FromMinutes(1)),
            Log("error.2026-10-14.log", TimeSpan.FromDays(13)),
            Log("error.2026-10-27.log", TimeSpan.Zero),
        ];

        var expired = Retention.Expired(files, Now, TimeSpan.FromDays(14));

        Assert.Equal([files[0].FullName, files[1].FullName], expired);
    }

    [Fact]
    public void Keeps_newest_30_backups()
    {
        // October 1 to 31, then November 1 to 4, in no particular order.
        var dirs = Enumerable.Range(1, 35).Select(n => n <= 31 ? $"2026-10-{n:00}-120000" : $"2026-11-{n - 31:00}-120000")
            .OrderBy(n => n.GetHashCode()).Select(n => Path.Combine(_dir, n)).ToList();

        var beyond = Retention.BeyondNewest(dirs, 30);

        Assert.Equal(Enumerable.Range(1, 5).Select(d => Path.Combine(_dir, $"2026-10-{d:00}-120000")).Order(), beyond.Order());
    }

    [Fact]
    public void Snapshots_from_the_same_second_sort_by_their_number()
    {
        var dirs = new[] { "2026-10-27-120000", "2026-10-27-120000-2", "2026-10-27-120000-10" }.Select(n => Path.Combine(_dir, n));

        Assert.Equal([Path.Combine(_dir, "2026-10-27-120000")], Retention.BeyondNewest(dirs, 2));
    }

    [Fact]
    public void Fewer_backups_than_kept_are_all_kept()
    {
        Assert.Empty(Retention.BeyondNewest([Path.Combine(_dir, "2026-10-27-120000")], 30));
    }
}
