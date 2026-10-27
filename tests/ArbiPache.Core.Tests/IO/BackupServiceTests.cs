using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;
using Microsoft.Extensions.Time.Testing;

namespace ArbiPache.Core.Tests.IO;

public sealed class BackupServiceTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-backup-" + Guid.NewGuid().ToString("N")));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 14, 2, 9, TimeSpan.Zero));

    public BackupServiceTests()
    {
        _paths.EnsureCreated();
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, recursive: true);
    }

    private string File1 => Path.Combine(_paths.Conf, "mode.conf");
    private string File2 => Path.Combine(_paths.PhpConfigDir("8.5.12"), "conf.d", "99-arbipache-mode.ini");
    private string Missing => Path.Combine(_paths.Conf, "db-mode.cnf");

    [Fact]
    public void Restore_writes_original_paths()
    {
        File.WriteAllText(File1, "Timeout 300\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(File2)!);
        File.WriteAllBytes(File2, [0xEF, 0xBB, 0xBF, (byte)'a', 0x0D, 0x0A]);
        var backups = new BackupService(_paths, _time);

        var snapshot = backups.Snapshot([File1, File2, Missing], "Development to Production");
        File.WriteAllText(File1, "Timeout 60\r\n");
        File.Delete(File2);
        File.WriteAllText(Missing, "[mysqld]\r\n");
        backups.Restore(snapshot);

        Assert.Equal("Timeout 300\r\n", File.ReadAllText(File1));
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'a', 0x0D, 0x0A], File.ReadAllBytes(File2));
        Assert.False(File.Exists(Missing));
    }

    [Fact]
    public void Snapshot_folder_is_named_by_local_time_and_has_a_manifest()
    {
        File.WriteAllText(File1, "Timeout 300\r\n");
        var backups = new BackupService(_paths, _time);

        Assert.Equal(Path.Combine(_paths.Backups, "2026-10-27-140209"), backups.NextSnapshot());
        var first = backups.Snapshot([File1], "Development to Production");
        var second = backups.Snapshot([File1], "Production to Development");

        Assert.Equal(Path.Combine(_paths.Backups, "2026-10-27-140209"), first);
        Assert.Equal(Path.Combine(_paths.Backups, "2026-10-27-140209-2"), second);
        Assert.True(File.Exists(Path.Combine(first, "manifest.json")));
        Assert.Equal([second, first], backups.List());
    }

    [Fact]
    public void Snapshot_records_paths_inside_the_root_relative_so_a_moved_folder_restores_in_place()
    {
        File.WriteAllText(File1, "Timeout 300\r\n");
        var snapshot = new BackupService(_paths, _time).Snapshot([File1], "Development to Production");
        var moved = new AppPaths(_paths.Root + "-moved");
        Directory.Move(_paths.Root, moved.Root);
        try
        {
            File.WriteAllText(Path.Combine(moved.Conf, "mode.conf"), "Timeout 60\r\n");

            new BackupService(moved, _time).Restore(snapshot.Replace(_paths.Root, moved.Root));

            Assert.Equal("Timeout 300\r\n", File.ReadAllText(Path.Combine(moved.Conf, "mode.conf")));
        }
        finally
        {
            Directory.Delete(moved.Root, recursive: true);
        }
    }

    [Fact]
    public void List_puts_the_newest_snapshot_first()
    {
        File.WriteAllText(File1, "Timeout 300\r\n");
        var backups = new BackupService(_paths, _time);
        var first = backups.Snapshot([File1], "Development to Production");
        var second = backups.Snapshot([File1], "Production to Development");
        _time.Advance(TimeSpan.FromDays(1));
        var third = backups.Snapshot([File1], "Development to Production");

        Assert.Equal([third, second, first], backups.List());
    }

    [Fact]
    public void List_ignores_folders_that_are_not_snapshots()
    {
        Directory.CreateDirectory(Path.Combine(_paths.Backups, "db"));
        Directory.CreateDirectory(Path.Combine(_paths.Backups, "2026-01-01-000000"));

        Assert.Empty(new BackupService(_paths, _time).List());
    }

    [Fact]
    public void Restore_refuses_a_file_changed_inside_the_snapshot()
    {
        File.WriteAllText(File1, "Timeout 300\r\n");
        var backups = new BackupService(_paths, _time);
        var snapshot = backups.Snapshot([File1], "Development to Production");
        foreach (var copy in Directory.GetFiles(Path.Combine(snapshot, "files"))) File.WriteAllText(copy, "tampered");
        File.WriteAllText(File1, "Timeout 60\r\n");

        var e = Assert.Throws<InvalidDataException>(() => backups.Restore(snapshot));

        Assert.Contains("mode.conf", e.Message, StringComparison.Ordinal);
        Assert.Equal("Timeout 60\r\n", File.ReadAllText(File1));
    }
}
