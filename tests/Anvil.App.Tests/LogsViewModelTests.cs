using Anvil.App.Services;
using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core.Paths;

namespace Anvil.App.Tests;

public sealed class LogsViewModelTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-logsvm-" + Guid.NewGuid().ToString("N")));

    public LogsViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_paths.Logs, "apache"));
        File.WriteAllText(Path.Combine(_paths.Logs, "apache", "error.log"),
            "[Tue Oct 27 14:02:12 2026] [mpm_winnt:notice] Child: Starting 256 worker threads.\n" +
            "[Tue Oct 27 14:03:01 2026] [core:error] [client 127.0.0.1] AH00126: Invalid URI in request\n" +
            "[Tue Oct 27 14:03:05 2026] [ssl:warn] AH01909: certificate does not include an ID which matches the server name\n");
        Directory.CreateDirectory(Path.Combine(_paths.Logs, "sites", "shop"));
        File.WriteAllText(Path.Combine(_paths.Logs, "sites", "shop", "error.log"), "PHP Fatal error:  Uncaught Error in index.php:3\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private LogsViewModel Make() => new(_paths, new EditorLauncher(() => "auto", new NoRegistry(), _ => false, ""), ui: a => a()) { Follow = false };

    [Fact]
    public void Sources_start_with_all_then_each_log()
    {
        using var vm = Make();

        vm.Refresh();

        Assert.Equal(["All", "Apache", "shop"], vm.Sources.Select(s => s.Label));
        Assert.Equal("All", vm.Selected?.Label);
        Assert.Equal(4, vm.Lines.Count);
    }

    [Fact]
    public void Errors_only_hides_other_lines()
    {
        using var vm = Make();
        vm.Refresh();
        vm.SelectCommand.Execute(vm.Sources.Single(s => s.Label == "Apache"));
        Assert.Equal(3, vm.Lines.Count);

        vm.ErrorsOnly = true;

        var line = Assert.Single(vm.Lines);
        Assert.Contains("AH00126", line.Text);
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Equal("Apache", line.Source);
    }

    [Fact]
    public void Levels_are_read_from_each_format()
    {
        Assert.Equal(LogLevel.Error, LogLine.LevelOf("PHP Fatal error:  Uncaught Error"));
        Assert.Equal(LogLevel.Error, LogLine.LevelOf("2026-10-27 14:00:00 0 [ERROR] mariadbd: Table is marked as crashed"));
        Assert.Equal(LogLevel.Warning, LogLine.LevelOf("[ssl:warn] AH01909"));
        Assert.Equal(LogLevel.Warning, LogLine.LevelOf("PHP Warning:  Undefined variable $x"));
        Assert.Equal(LogLevel.Info, LogLine.LevelOf("[mpm_winnt:notice] Child: Starting 256 worker threads."));
    }
}
