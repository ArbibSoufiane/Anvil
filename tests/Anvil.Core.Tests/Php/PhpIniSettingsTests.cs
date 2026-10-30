using Anvil.Core.IO;
using Anvil.Core.Paths;
using Anvil.Core.Php;

namespace Anvil.Core.Tests.Php;

public sealed class PhpIniSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-phpset-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public PhpIniSettingsTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        foreach (var version in new[] { "8.4.27", "8.5.12" })
        {
            Directory.CreateDirectory(Path.Combine(_paths.PhpRuntime(version), "ext"));
            Directory.CreateDirectory(_paths.PhpConfigDir(version));
        }
        File.WriteAllText(Path.Combine(_paths.PhpRuntime("8.4.27"), "ext", "php_opcache.dll"), "");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string IniFile(string version) => Path.Combine(_paths.PhpConfigDir(version), "php.ini");

    [Fact]
    public void Reads_the_values_php_ini_sets_without_their_comments()
    {
        File.WriteAllText(IniFile("8.4.27"), "[PHP]\r\nmemory_limit = 1G ; for Composer\r\n;max_input_vars = 1000\r\npost_max_size = \"256M\"\r\n");

        var values = new PhpIniSettings(_paths).Read("8.4.27");

        Assert.Equal(new Dictionary<string, string> { ["memory_limit"] = "1G", ["post_max_size"] = "256M" }, values.Values);
        Assert.False(values.Opcache);
        Assert.True(values.OpcacheAvailable);
    }

    [Fact]
    public void Writes_values_in_place_and_an_empty_one_leaves_it_to_php()
    {
        File.WriteAllText(IniFile("8.4.27"), "[PHP]\r\n; Maximum amount of memory\r\nmemory_limit = 128M\r\n;max_input_vars = 1000\r\nupload_max_filesize = 2M\r\n");

        new PhpIniSettings(_paths).Write("8.4.27", new Dictionary<string, string>
        {
            ["memory_limit"] = "1g", ["max_input_vars"] = "5000", ["upload_max_filesize"] = "", ["realpath_cache_ttl"] = "600",
        });

        Assert.Equal("[PHP]\r\n; Maximum amount of memory\r\nmemory_limit = 1G\r\nmax_input_vars = 5000\r\nrealpath_cache_ttl = 600\r\n",
            File.ReadAllText(IniFile("8.4.27")));
    }

    [Theory]
    [InlineData("memory_limit", "-1", null)]
    [InlineData("memory_limit", "1.5G", "memory_limit must be a size such as 256M or 1G, or -1 for no limit.")]
    [InlineData("post_max_size", "-1", "post_max_size must be a size such as 256M or 1G.")]
    [InlineData("max_execution_time", "300s", "max_execution_time must be a whole number.")]
    [InlineData("max_input_time", "-1", null)]
    [InlineData("realpath_cache_size", "16M", null)]
    [InlineData("display_errors", "On", "display_errors is not a setting the PHP page edits. Edit php.ini for it.")]
    public void Refuses_values_php_would_not_read(string key, string value, string? problem)
    {
        Assert.Equal(problem, PhpIniSettings.Problem(key, value));
        if (problem is null) return;
        File.WriteAllText(IniFile("8.4.27"), "[PHP]\r\n");
        Assert.Throws<ArgumentException>(() => new PhpIniSettings(_paths).Write("8.4.27", new Dictionary<string, string> { [key] = value }));
        Assert.Equal("[PHP]\r\n", File.ReadAllText(IniFile("8.4.27")));
    }

    [Fact]
    public void Opcache_loads_and_switches_on_and_off()
    {
        File.WriteAllText(IniFile("8.4.27"), "[PHP]\r\nextension=curl\r\n\r\n[opcache]\r\n;opcache.enable=1\r\n");
        var settings = new PhpIniSettings(_paths);

        settings.SetOpcache("8.4.27", enabled: true);

        var ini = IniDocument.Parse(File.ReadAllText(IniFile("8.4.27")));
        Assert.Equal(["opcache"], ini.GetAll("PHP", "zend_extension"));
        Assert.Equal("1", ini.Get("opcache", "opcache.enable"));
        Assert.True(settings.Read("8.4.27").Opcache);

        settings.SetOpcache("8.4.27", enabled: false);

        Assert.False(settings.Read("8.4.27").Opcache);
        Assert.Equal(["opcache"], IniDocument.Parse(File.ReadAllText(IniFile("8.4.27"))).GetAll("PHP", "zend_extension"));
    }

    [Fact]
    public void Php_8_5_has_opcache_built_in()
    {
        File.WriteAllText(IniFile("8.5.12"), "[PHP]\r\n");
        var settings = new PhpIniSettings(_paths);
        Assert.True(settings.Read("8.5.12").Opcache);

        settings.SetOpcache("8.5.12", enabled: false);

        Assert.False(settings.Read("8.5.12").Opcache);
        Assert.Empty(IniDocument.Parse(File.ReadAllText(IniFile("8.5.12"))).GetAll("PHP", "zend_extension"));
    }

    [Fact]
    public void Opcache_without_its_dll_is_refused()
    {
        File.Delete(Path.Combine(_paths.PhpRuntime("8.4.27"), "ext", "php_opcache.dll"));
        File.WriteAllText(IniFile("8.4.27"), "[PHP]\r\n");

        Assert.False(new PhpIniSettings(_paths).Read("8.4.27").OpcacheAvailable);
        var e = Assert.Throws<ArgumentException>(() => new PhpIniSettings(_paths).SetOpcache("8.4.27", enabled: true));
        Assert.Equal("PHP 8.4.27 has no php_opcache.dll in its ext folder.", e.Message);
    }
}
