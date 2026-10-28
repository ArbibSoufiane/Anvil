using Anvil.Core.Modes;
using Anvil.Core.Settings;

namespace Anvil.Core.Tests.Modes;

public sealed class BuiltInProfilesTests
{
    private static readonly ModeProfile Dev = BuiltInProfiles.Development;
    private static readonly ModeProfile Prod = BuiltInProfiles.Production;

    [Fact]
    public void Production_hides_errors()
    {
        Assert.Equal("Off", Prod.Php["display_errors"]);
        Assert.Equal("Off", Prod.Php["display_startup_errors"]);
        Assert.Equal("E_ALL & ~E_DEPRECATED", Prod.Php["error_reporting"]);
        Assert.Equal("Off", Prod.Php["expose_php"]);
        Assert.Equal("Prod", Prod.Apache["ServerTokens"]);
        Assert.Equal("Off", Prod.Apache["ServerSignature"]);
        Assert.Equal("Off", Prod.Apache["TraceEnable"]);
    }

    [Fact]
    public void Production_limits()
    {
        Assert.Equal("30", Prod.Php["max_execution_time"]);
        Assert.Equal("60", Prod.Php["max_input_time"]);
        Assert.Equal("256M", Prod.Php["memory_limit"]);
        Assert.Equal("32M", Prod.Php["post_max_size"]);
        Assert.Equal("32M", Prod.Php["upload_max_filesize"]);
        Assert.Equal("60", Prod.Apache["Timeout"]);
        Assert.Equal("header=20-40,MinRate=500 body=20,MinRate=500", Prod.Apache["RequestReadTimeout"]);
        Assert.Equal("33554432", Prod.Apache["LimitRequestBody"]);
        Assert.Equal("60", Prod.Apache["PhpBackendTimeout"]);
        Assert.Equal("60", Prod.Database["statement_timeout"]);
        Assert.Equal("600", Prod.Database["wait_timeout"]);
        Assert.False(Prod.Xdebug);
        Assert.True(Prod.SecurityHeaders);
        Assert.False(Prod.DirectoryListing);
    }

    [Fact]
    public void Production_disables_shell_functions() =>
        Assert.Equal("exec,shell_exec,system,passthru,proc_open,popen", Prod.Php["disable_functions"]);

    [Fact]
    public void Development_long_limits()
    {
        Assert.Equal("300", Dev.Php["max_execution_time"]);
        Assert.Equal("1G", Dev.Php["memory_limit"]);
        Assert.Equal("512M", Dev.Php["post_max_size"]);
        Assert.Equal("On", Dev.Php["display_errors"]);
        Assert.Equal("", Dev.Php["disable_functions"]);
        Assert.Equal("300", Dev.Apache["Timeout"]);
        Assert.Equal("header=0 body=0", Dev.Apache["RequestReadTimeout"]);
        Assert.Equal("536870912", Dev.Apache["LimitRequestBody"]);
        Assert.Equal("0", Dev.Database["statement_timeout"]);
        Assert.Equal("ON", Dev.Database["general_log"]);
        Assert.True(Dev.Xdebug);
        Assert.False(Dev.SecurityHeaders);
        Assert.True(Dev.DirectoryListing);
    }

    [Fact]
    public void Profiles_cover_same_keys()
    {
        Assert.Equal(Dev.Php.Keys.Order(), Prod.Php.Keys.Order());
        Assert.Equal(Dev.Apache.Keys.Order(), Prod.Apache.Keys.Order());
        Assert.Equal(Dev.Database.Keys.Order(), Prod.Database.Keys.Order());
        Assert.Equal(15, Dev.Php.Count);
        Assert.Equal(8, Dev.Apache.Count);
        Assert.Equal(5, Dev.Database.Count);
    }

    [Fact]
    public void For_returns_matching_profile()
    {
        Assert.Same(Prod, BuiltInProfiles.For(ServerMode.Production));
        Assert.Same(Dev, BuiltInProfiles.For(ServerMode.Development));
        Assert.Equal(ServerMode.Production, Prod.Mode);
    }
}
