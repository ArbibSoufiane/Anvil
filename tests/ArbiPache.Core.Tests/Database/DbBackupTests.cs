using ArbiPache.Core.Database;
using ArbiPache.Core.Paths;
using Microsoft.Extensions.Time.Testing;

namespace ArbiPache.Core.Tests.Database;

public sealed class DbBackupTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), $"dbbackup {Guid.NewGuid():N}"));

    public DbBackupTests() => _paths.EnsureCreated();

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    [Fact]
    public void Prune_keeps_newest_14()
    {
        var dir = Path.Combine(_paths.Backups, "db");
        Directory.CreateDirectory(dir);
        var start = new DateTime(2026, 10, 1, 3, 15, 0);
        // Written newest first, so file times disagree with the names: the name is what counts.
        for (var day = 19; day >= 0; day--) File.WriteAllText(Path.Combine(dir, $"{start.AddDays(day):yyyy-MM-dd-HHmmss}.sql.gz"), "");
        File.WriteAllText(Path.Combine(dir, "before-migration.sql.gz"), "");

        var removed = DbBackup.Prune(dir);

        Assert.Equal(6, removed.Count);
        var kept = Directory.GetFiles(dir).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(15, kept.Count);
        Assert.Contains("before-migration.sql.gz", kept);
        Assert.Equal("2026-10-07-031500.sql.gz", kept[0]);
        Assert.Equal("2026-10-20-031500.sql.gz", kept[^2]);
    }

    [Fact]
    public void Backup_file_name_uses_timestamp()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 23, 4, 5, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("AST", TimeSpan.FromHours(4), "AST", "AST"));
        var backup = new DbBackup(_paths, new DbSecrets(_paths), time);

        var first = backup.NextFile();
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        File.WriteAllText(first, "");
        var second = backup.NextFile();

        Assert.Equal(Path.Combine(_paths.Backups, "db", "2026-10-28-030405.sql.gz"), first);
        Assert.Equal(Path.Combine(_paths.Backups, "db", "2026-10-28-030405-2.sql.gz"), second);
    }
}
