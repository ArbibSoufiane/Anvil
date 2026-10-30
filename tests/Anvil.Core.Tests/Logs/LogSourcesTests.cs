using Anvil.Core.Logs;

namespace Anvil.Core.Tests.Logs;

public sealed class LogSourcesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ap-logclear-" + Guid.NewGuid().ToString("N"));

    public LogSourcesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Clear_empties_logs_their_writers_keep_open_and_names_the_ones_it_could_not()
    {
        var apache = Path.Combine(_dir, "error.log");
        var locked = Path.Combine(_dir, "agent.log");
        File.WriteAllText(apache, "[core:error] AH00126\n");
        File.WriteAllText(locked, "started\n");
        using var writer = new FileStream(apache, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        var kept = LogSources.Clear([new("apache", "Apache", apache), new("anvil", "Anvil", locked), new("db", "Database", Path.Combine(_dir, "none.log"))]);

        Assert.Equal([locked], kept);
        Assert.Equal(0, new FileInfo(apache).Length);
        Assert.Equal(8, new FileInfo(locked).Length);
    }
}
