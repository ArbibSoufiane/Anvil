using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Modes;
using Anvil.Core.Tests.Support;

namespace Anvil.Core.Tests.Modes;

public sealed class ModeRenderTests : IDisposable
{
    private const string Dll = @"D:\Anvil\runtime\xdebug\3.5.3-php8.5\php_xdebug-3.5.3-8.5-nts-vs17-x86_64.dll";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-mode-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Production_php_ini()
    {
        var ini = PhpModeIniRenderer.Render(BuiltInProfiles.Production, "8.5.12", Dll);

        Snapshot.Match(ini, "production-php");
        Assert.DoesNotContain("xdebug", ini, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Development_php_ini_with_xdebug()
    {
        var ini = PhpModeIniRenderer.Render(BuiltInProfiles.Development, "8.5.12", Dll);

        Snapshot.Match(ini, "development-php-xdebug");
        Assert.Contains("zend_extension=\"D:/Anvil/runtime/xdebug/3.5.3-php8.5/php_xdebug-3.5.3-8.5-nts-vs17-x86_64.dll\"", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void Xdebug_skipped_when_dll_missing()
    {
        var ini = PhpModeIniRenderer.Render(BuiltInProfiles.Development, "8.5.12", null);

        Assert.DoesNotContain("xdebug", ini, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("display_errors = On", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void Php_values_with_line_breaks_are_refused()
    {
        var profile = BuiltInProfiles.Development with
        {
            Php = new Dictionary<string, string>(BuiltInProfiles.Development.Php) { ["memory_limit"] = "1G\nauto_prepend_file = evil.php" },
        };

        var e = Assert.Throws<ArgumentException>(() => PhpModeIniRenderer.Render(profile, "8.5.12", null));

        Assert.Equal("The Development profile's PHP value for memory_limit cannot contain line breaks.", e.Message);
    }

    [Fact]
    public void Xdebug_dll_is_the_build_for_the_php_minor()
    {
        var folder = Path.Combine(_root, "xdebug", "3.5.3-php8.5");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "php_xdebug-3.5.3-8.5-nts-vs17-x86_64.dll"), "");
        var empty = Path.Combine(_root, "xdebug", "3.5.3-php8.4");
        Directory.CreateDirectory(empty);
        InstalledComponent[] installed =
        [
            new(ComponentKind.Xdebug, "3.5.3", folder, new string('a', 64), DateTimeOffset.UtcNow, AppliesTo: "8.5"),
            new(ComponentKind.Xdebug, "3.5.3", empty, new string('a', 64), DateTimeOffset.UtcNow, AppliesTo: "8.4"),
        ];

        Assert.Equal(Path.Combine(folder, "php_xdebug-3.5.3-8.5-nts-vs17-x86_64.dll"), PhpModeIniRenderer.XdebugDll(installed, "8.5.12"));
        Assert.Null(PhpModeIniRenderer.XdebugDll(installed, "8.4.23"));
        Assert.Null(PhpModeIniRenderer.XdebugDll(installed, "8.3.20"));
    }

    [Fact]
    public void MariaDb_statement_time_seconds()
    {
        var cnf = DbModeRenderer.Render(BuiltInProfiles.Production, DbEngine.MariaDb);

        Snapshot.Match(cnf, "production-mariadb");
        Assert.Contains("max_statement_time = 60\n", cnf.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("max_execution_time", cnf, StringComparison.Ordinal);
        Assert.DoesNotContain("statement_timeout", cnf, StringComparison.Ordinal);
    }

    [Fact]
    public void MySql_execution_time_milliseconds()
    {
        var production = DbModeRenderer.Render(BuiltInProfiles.Production, DbEngine.MySql).ReplaceLineEndings("\n");
        var development = DbModeRenderer.Render(BuiltInProfiles.Development, DbEngine.MySql).ReplaceLineEndings("\n");
        var half = BuiltInProfiles.Production with
        {
            Database = new Dictionary<string, string>(BuiltInProfiles.Production.Database) { ["statement_timeout"] = "2.5" },
        };

        Assert.Contains("max_execution_time = 60000\n", production, StringComparison.Ordinal);
        Assert.DoesNotContain("max_statement_time", production, StringComparison.Ordinal);
        Assert.Contains("max_execution_time = 0\n", development, StringComparison.Ordinal);
        Assert.Contains("wait_timeout = 28800\n", development, StringComparison.Ordinal);
        Assert.Contains("max_execution_time = 2500\n", DbModeRenderer.Render(half, DbEngine.MySql).ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Statement_timeout_must_be_seconds()
    {
        var bad = BuiltInProfiles.Production with
        {
            Database = new Dictionary<string, string>(BuiltInProfiles.Production.Database) { ["statement_timeout"] = "1 minute" },
        };

        var e = Assert.Throws<ArgumentException>(() => DbModeRenderer.Render(bad, DbEngine.MySql));

        Assert.Equal("The Production profile's statement_timeout must be a number of seconds, not \"1 minute\".", e.Message);
        var negative = bad with { Database = new Dictionary<string, string>(bad.Database) { ["statement_timeout"] = "-1" } };
        Assert.Throws<ArgumentException>(() => DbModeRenderer.Render(negative, DbEngine.MariaDb));
    }
}
