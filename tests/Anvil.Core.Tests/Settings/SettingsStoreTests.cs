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
    public void Settings_written_by_a_service_build_still_load()
    {
        var store = new SettingsStore(_paths);
        File.WriteAllText(Path.Combine(_paths.Data, "anvil.json"), """{"schemaVersion":1,"runAsService":true,"controlUserSids":["S-1-5-21-1"],"httpPort":8080}""");

        Assert.Equal(8080, store.Load().HttpPort);
        Assert.Null(store.LastLoadWarning);
    }

    [Fact]
    public void Defaults_match_spec()
    {
        var s = new SettingsStore(_paths).Load();

        Assert.Equal(80, s.HttpPort);
        Assert.Equal(443, s.HttpsPort);
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
            ActiveApache = "2.4.65",
            DefaultPhp = "8.5.5",
            DbEngine = DbEngine.MariaDb,
            DbVersion = "11.4.8",
            Apache = new ApacheTuning(256, 1000, 3),
            OptionalModules = ["deflate"],
            PhpPools = new Dictionary<string, PhpPoolSettings> { ["8.5.5"] = new(8, 500) },
            PhpPoolPorts = new Dictionary<string, int> { ["php85"] = 9100 },
            Network = new NetworkAccess(false),
            NetworkDomain = "arbib.pc",
        };

        store.Save(saved);
        var loaded = new SettingsStore(_paths).Load();

        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(loaded));
        Assert.Contains("\"httpPort\": 8080", File.ReadAllText(Path.Combine(_paths.Data, "anvil.json")));
    }

    [Fact]
    public void Settings_from_a_build_with_modes_still_load()
    {
        File.WriteAllText(Path.Combine(_paths.Data, "anvil.json"), "{\"httpPort\": 8080, \"mode\": \"Production\", \"xdebugInProduction\": true}");
        var store = new SettingsStore(_paths);

        Assert.Equal(8080, store.Load().HttpPort);
        Assert.Null(store.LastLoadWarning);
    }

    [Fact]
    public void Try_read_gives_defaults_without_a_file_and_null_for_a_corrupt_one_and_moves_nothing()
    {
        var store = new SettingsStore(_paths);
        Assert.Equal(80, store.TryRead()?.HttpPort);

        store.Save(new AppSettings { HttpPort = 8080 });
        Assert.Equal(8080, store.TryRead()?.HttpPort);

        var file = Path.Combine(_paths.Data, "anvil.json");
        File.WriteAllText(file, "{not json");
        Assert.Null(store.TryRead());
        Assert.Equal("{not json", File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(_paths.Data, "anvil.json*"));
    }

    [Fact]
    public void Old_network_settings_keep_the_htdocs_switch()
    {
        File.WriteAllText(Path.Combine(_paths.Data, "anvil.json"), "{\"network\": {\"htdocsOnNetwork\": false, \"privateNetworksOnly\": true}}");

        Assert.Equal(new NetworkAccess(false), new SettingsStore(_paths).Load().Network);
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
