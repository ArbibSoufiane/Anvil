using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;

namespace ArbiPache.Core.Tests.Php;

public sealed class PhpIniBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-phpini-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public PhpIniBootstrapTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        var ca = Path.Combine(_paths.Runtime, "cabundle", "2026-10-20");
        Directory.CreateDirectory(ca);
        File.WriteAllText(Path.Combine(ca, "cacert.pem"), "-----BEGIN CERTIFICATE-----");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // The shape of php.ini-development: everything commented out, CRLF line ends.
    private void InstallPhp(string version)
    {
        var runtime = _paths.PhpRuntime(version);
        Directory.CreateDirectory(Path.Combine(runtime, "ext"));
        var extensions = "curl exif ffi fileinfo gd intl mbstring mysqli openssl pdo_mysql pdo_sqlite sodium zip"
            .Split(' ').Select(e => ";extension=" + e);
        var opcache = version.StartsWith("8.5", StringComparison.Ordinal) ? [] : new[] { ";zend_extension=opcache" };
        File.WriteAllText(Path.Combine(runtime, "php.ini-development"), string.Join("\r\n", new[]
        {
            "[PHP]", "memory_limit = 128M", ";error_log = php_errors.log", ";extension_dir = \"./\"", ";extension_dir = \"ext\"",
            ";sys_temp_dir = \"/tmp\"", ";upload_tmp_dir =",
        }.Concat(extensions).Concat(opcache).Concat(new[]
        {
            "", "[Session]", ";session.save_path = \"/tmp\"", "", "[curl]", ";curl.cainfo =", "", "[openssl]", ";openssl.cafile=", "",
        })));
    }

    private IniDocument Ini(string version) => IniDocument.Parse(File.ReadAllText(Path.Combine(_paths.PhpConfigDir(version), "php.ini")));

    [Fact]
    public void EnsureIni_sets_absolute_extension_dir()
    {
        InstallPhp("8.5.12");

        new PhpIniBootstrap(_paths).EnsureIni("8.5.12");

        Assert.Equal(AppPaths.ForApache(Path.Combine(_paths.PhpRuntime("8.5.12"), "ext")), Ini("8.5.12").Get("PHP", "extension_dir"));
        Assert.True(Directory.Exists(Path.Combine(_paths.PhpConfigDir("8.5.12"), "conf.d")));
    }

    [Fact]
    public void EnsureIni_enables_default_extensions()
    {
        InstallPhp("8.5.12");

        new PhpIniBootstrap(_paths).EnsureIni("8.5.12");

        Assert.Equal(["curl", "exif", "fileinfo", "gd", "intl", "mbstring", "mysqli", "openssl", "pdo_mysql", "sodium", "zip"],
            Ini("8.5.12").GetAll("PHP", "extension").Order());
    }

    [Fact]
    public void Opcache_line_only_before_8_5()
    {
        InstallPhp("8.4.27");
        InstallPhp("8.5.12");
        var bootstrap = new PhpIniBootstrap(_paths);

        bootstrap.EnsureIni("8.4.27");
        bootstrap.EnsureIni("8.5.12");

        Assert.Equal(["opcache"], Ini("8.4.27").GetAll("PHP", "zend_extension"));
        Assert.Empty(Ini("8.5.12").GetAll("PHP", "zend_extension"));
    }

    [Fact]
    public void EnsureIni_points_logs_sessions_temp_and_ca_bundle_into_the_portable_folder()
    {
        InstallPhp("8.5.12");

        new PhpIniBootstrap(_paths).EnsureIni("8.5.12");

        var ini = Ini("8.5.12");
        var data = AppPaths.ForApache(_paths.Data);
        Assert.Equal($"{data}/logs/php/8.5.12.log", ini.Get("PHP", "error_log"));
        Assert.Equal($"{data}/tmp/php-sessions/8.5.12", ini.Get("Session", "session.save_path"));
        Assert.Equal($"{data}/tmp/php-uploads/8.5.12", ini.Get("PHP", "upload_tmp_dir"));
        Assert.Equal($"{data}/tmp/php/8.5.12", ini.Get("PHP", "sys_temp_dir"));
        var ca = AppPaths.ForApache(Path.Combine(_paths.Runtime, "cabundle", "2026-10-20", "cacert.pem"));
        Assert.Equal(ca, ini.Get("curl", "curl.cainfo"));
        Assert.Equal(ca, ini.Get("openssl", "openssl.cafile"));
        Assert.True(Directory.Exists(Path.Combine(_paths.Tmp, "php-sessions", "8.5.12")));
        Assert.True(Directory.Exists(Path.Combine(_paths.Logs, "php")));
    }

    [Fact]
    public void EnsureIni_does_not_overwrite_existing_user_ini()
    {
        InstallPhp("8.5.12");
        var bootstrap = new PhpIniBootstrap(_paths);
        bootstrap.EnsureIni("8.5.12");
        var file = Path.Combine(_paths.PhpConfigDir("8.5.12"), "php.ini");
        var edited = IniDocument.Parse(File.ReadAllText(file));
        edited.Set("PHP", "memory_limit", "1G");
        edited.RemoveValue("PHP", "extension", "gd");
        File.WriteAllText(file, edited.ToString());

        bootstrap.EnsureIni("8.5.12");

        Assert.Equal(edited.ToString(), File.ReadAllText(file));
    }

    [Fact]
    public void EnsureIni_repoints_paths_after_the_folder_moves()
    {
        InstallPhp("8.5.12");
        new PhpIniBootstrap(_paths).EnsureIni("8.5.12");
        var file = Path.Combine(_paths.PhpConfigDir("8.5.12"), "php.ini");
        File.WriteAllText(file, File.ReadAllText(file).Replace(AppPaths.ForApache(_root), "D:/OldPlace", StringComparison.Ordinal));

        new PhpIniBootstrap(_paths).EnsureIni("8.5.12");

        var ini = Ini("8.5.12");
        Assert.Equal(AppPaths.ForApache(Path.Combine(_paths.PhpRuntime("8.5.12"), "ext")), ini.Get("PHP", "extension_dir"));
        Assert.DoesNotContain("D:/OldPlace", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureIni_requires_the_ca_bundle()
    {
        InstallPhp("8.5.12");
        Directory.Delete(Path.Combine(_paths.Runtime, "cabundle"), recursive: true);

        var e = Assert.Throws<InvalidOperationException>(() => new PhpIniBootstrap(_paths).EnsureIni("8.5.12"));
        Assert.Contains("CA bundle", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EOL_for_8_1_on_2026_10_27_is_true()
    {
        var today = new DateOnly(2026, 10, 27);

        Assert.True(PhpSupport.IsEndOfLife("8.1.34", today));
        Assert.True(PhpSupport.IsEndOfLife("7.4.33", today));
        Assert.False(PhpSupport.IsEndOfLife("8.2.35", today));
        Assert.False(PhpSupport.IsEndOfLife("8.5.12", today));
        Assert.True(PhpSupport.HasBuiltInOpcache("8.5.12"));
        Assert.False(PhpSupport.HasBuiltInOpcache("8.4.27"));
    }
}
