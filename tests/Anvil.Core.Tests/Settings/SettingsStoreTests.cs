using System.Text.Json;
using Anvil.Core.Database;
using Anvil.Core.Paths;
using Anvil.Core.Settings;

namespace Anvil.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-settings-" + Guid.NewGuid().ToString("N")));

    public SettingsStoreTests() => _paths.EnsureCreated();

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, recursive: true);
    }

    [Fact]
    public void Defaults_match_spec()
    {
        var s = new SettingsStore(_paths).Load();

        Assert.Equal(80, s.HttpPort);
        Assert.Equal(443, s.HttpsPort);
        Assert.Equal(ServerMode.Development, s.Mode);
        Assert.True(s.CloseToTray);
        Assert.True(s.StartServicesOnLaunch);
        Assert.Equal(3306, s.DbPort);
    }

    [Fact]
    public void Round_trip_preserves_values()
    {
        var store = new SettingsStore(_paths);
        var saved = new AppSettings
        {
            HttpPort = 8080,
            Mode = ServerMode.Production,
            ActiveApache = "2.4.65",
            DefaultPhp = "8.5.5",
            DbEngine = DbEngine.MariaDb,
            DbVersion = "11.4.8",
            Apache = new ApacheTuning(256, 1000, 3),
            OptionalModules = ["deflate"],
            PhpPools = new Dictionary<string, PhpPoolSettings> { ["8.5.5"] = new(8, 500) },
            PhpPoolPorts = new Dictionary<string, int> { ["php85"] = 9100 },
            Network = new NetworkAccess(false, true),
            AcmeEmail = "ops@example.com",
            ControlUserSids = ["S-1-5-21-1"],
        };

        store.Save(saved);
        var loaded = new SettingsStore(_paths).Load();

        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(loaded));
        Assert.Contains("\"mode\": \"Production\"", File.ReadAllText(Path.Combine(_paths.Data, "anvil.json")));
    }

    [Fact]
    public void Corrupt_file_is_kept_aside_and_defaults_returned()
    {
        var file = Path.Combine(_paths.Data, "anvil.json");
        File.WriteAllText(file, "{ not json");
        var store = new SettingsStore(_paths);

        var s = store.Load();

        Assert.Equal(80, s.HttpPort);
        Assert.NotNull(store.LastLoadWarning);
        Assert.False(File.Exists(file));
        Assert.Single(Directory.GetFiles(_paths.Data, "anvil.json.corrupt-*"));
    }

    [Fact]
    public void Save_leaves_no_temp_file()
    {
        new SettingsStore(_paths).Save(new AppSettings());
        new SettingsStore(_paths).Save(new AppSettings { HttpPort = 81 });

        Assert.Equal(["anvil.json"], Directory.GetFiles(_paths.Data).Select(Path.GetFileName).Where(f => f!.StartsWith("anvil")));
        Assert.Equal(81, new SettingsStore(_paths).Load().HttpPort);
    }
}
