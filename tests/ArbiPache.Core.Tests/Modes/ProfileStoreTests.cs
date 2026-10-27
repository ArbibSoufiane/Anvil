using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;

namespace ArbiPache.Core.Tests.Modes;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-profiles-" + Guid.NewGuid().ToString("N")));

    public ProfileStoreTests() => _paths.EnsureCreated();

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, recursive: true);
    }

    private static void AssertSame(ModeProfile expected, ModeProfile actual)
    {
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.Php, actual.Php);
        Assert.Equal(expected.Apache, actual.Apache);
        Assert.Equal(expected.Database, actual.Database);
        Assert.Equal((expected.Xdebug, expected.SecurityHeaders, expected.DirectoryListing), (actual.Xdebug, actual.SecurityHeaders, actual.DirectoryListing));
    }

    [Fact]
    public void Missing_file_returns_builtin()
    {
        var store = new ProfileStore(_paths);

        AssertSame(BuiltInProfiles.Development, store.Get(ServerMode.Development));
        AssertSame(BuiltInProfiles.Production, store.Get(ServerMode.Production));
    }

    [Fact]
    public void Override_replaces_single_value()
    {
        var store = new ProfileStore(_paths);

        store.SaveOverrides(ServerMode.Production, new ModeProfileOverrides(
            Php: new Dictionary<string, string> { ["max_execution_time"] = "45" }, Apache: null, Database: null,
            Xdebug: null, SecurityHeaders: false, DirectoryListing: null));

        var p = store.Get(ServerMode.Production);
        Assert.Equal("45", p.Php["max_execution_time"]);
        Assert.Equal("256M", p.Php["memory_limit"]);
        Assert.Equal(BuiltInProfiles.Production.Php.Count, p.Php.Count);
        Assert.Equal(BuiltInProfiles.Production.Apache, p.Apache);
        Assert.Equal(BuiltInProfiles.Production.Database, p.Database);
        Assert.False(p.SecurityHeaders);
        Assert.False(p.Xdebug);
        Assert.False(p.DirectoryListing);
        Assert.True(File.Exists(Path.Combine(_paths.Profiles, "production.json")));
        AssertSame(BuiltInProfiles.Development, store.Get(ServerMode.Development));
    }

    [Fact]
    public void Override_keys_match_the_way_each_config_reads_them()
    {
        var store = new ProfileStore(_paths);

        store.SaveOverrides(ServerMode.Development, new ModeProfileOverrides(
            Php: new Dictionary<string, string> { ["max_input_vars"] = "5000" },
            Apache: new Dictionary<string, string> { ["timeout"] = "120" },
            Database: new Dictionary<string, string> { ["wait-timeout"] = "3600" },
            Xdebug: false, SecurityHeaders: null, DirectoryListing: null));

        var p = store.Get(ServerMode.Development);
        // Apache directives ignore case; MariaDB/MySQL options treat - and _ alike; php.ini keys are taken as written.
        Assert.Equal("120", p.Apache["Timeout"]);
        Assert.Equal(BuiltInProfiles.Development.Apache.Count, p.Apache.Count);
        Assert.Equal("3600", p.Database["wait_timeout"]);
        Assert.Equal(BuiltInProfiles.Development.Database.Count, p.Database.Count);
        Assert.Equal("5000", p.Php["max_input_vars"]);
        Assert.Equal(BuiltInProfiles.Development.Php.Count + 1, p.Php.Count);
        Assert.False(p.Xdebug);
        Assert.True(p.DirectoryListing);
    }

    [Fact]
    public void Reset_restores_builtin()
    {
        var store = new ProfileStore(_paths);
        store.SaveOverrides(ServerMode.Development, new ModeProfileOverrides(
            Php: new Dictionary<string, string> { ["memory_limit"] = "2G" }, Apache: null, Database: null, Xdebug: false, SecurityHeaders: null,
            DirectoryListing: null));

        store.Reset(ServerMode.Development);

        AssertSame(BuiltInProfiles.Development, store.Get(ServerMode.Development));
        Assert.False(File.Exists(Path.Combine(_paths.Profiles, "development.json")));
        store.Reset(ServerMode.Development);
    }

    [Fact]
    public void Unreadable_file_is_moved_aside_and_builtin_used()
    {
        Directory.CreateDirectory(_paths.Profiles);
        File.WriteAllText(Path.Combine(_paths.Profiles, "production.json"), "{ \"php\": ");
        var store = new ProfileStore(_paths);

        AssertSame(BuiltInProfiles.Production, store.Get(ServerMode.Production));

        Assert.False(File.Exists(Path.Combine(_paths.Profiles, "production.json")));
        Assert.Single(Directory.GetFiles(_paths.Profiles, "production.json.corrupt-*"));
        Assert.Contains("production.json.corrupt-", store.LastLoadWarning, StringComparison.Ordinal);
    }
}
