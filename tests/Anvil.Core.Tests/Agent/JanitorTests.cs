using System.Diagnostics;
using Anvil.Agent;
using Anvil.Core.Paths;
using Microsoft.Extensions.Time.Testing;

namespace Anvil.Core.Tests.Agent;

public sealed class JanitorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 4, 0, 0, TimeSpan.Zero);

    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-janitor-" + Guid.NewGuid().ToString("N")));
    private readonly string _outside;

    public JanitorTests()
    {
        _paths.EnsureCreated();
        _outside = Directory.CreateDirectory(_paths.Root + "-outside").FullName;
    }

    public void Dispose()
    {
        var link = Path.Combine(_paths.Logs, "elsewhere");
        if (Directory.Exists(link)) Directory.Delete(link);
        Directory.Delete(_paths.Root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private static string File(string dir, string name, TimeSpan age)
    {
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name);
        System.IO.File.WriteAllText(file, "");
        System.IO.File.SetLastWriteTimeUtc(file, (Now - age).UtcDateTime);
        return file;
    }

    [Fact]
    public void Clean_deletes_logs_unwritten_for_14_days()
    {
        var oldLog = File(Path.Combine(_paths.Logs, "sites", "shop"), "error.2026-10-01.log", TimeSpan.FromDays(26));
        var newLog = File(Path.Combine(_paths.Logs, "apache"), "error.2026-10-27.log", TimeSpan.Zero);
        var oldDump = File(Path.Combine(_paths.Backups, "db"), "2026-09-01-020000.sql.gz", TimeSpan.FromDays(56));

        new Janitor(_paths, new FakeTimeProvider(Now)).Clean();

        Assert.False(System.IO.File.Exists(oldLog));
        Assert.True(System.IO.File.Exists(newLog));
        // Database dumps keep their own count (DbBackup).
        Assert.True(System.IO.File.Exists(oldDump));
    }

    [Fact]
    public void Clean_does_not_follow_a_link_out_of_the_logs_folder()
    {
        var outsideLog = File(_outside, "keep.log", TimeSpan.FromDays(30));
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_paths.Logs, "elsewhere")}\" \"{_outside}\"")
               {
                   UseShellExecute = false,
                   CreateNoWindow = true,
                   RedirectStandardOutput = true,
               })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        new Janitor(_paths, new FakeTimeProvider(Now)).Clean();

        Assert.True(System.IO.File.Exists(outsideLog));
    }
}
