using Anvil.Core.Settings;

namespace Anvil.Core.Modes;

/// <summary>The spec's "Modes (pinned values)" table.</summary>
public static class BuiltInProfiles
{
    public static ModeProfile Development { get; } = new(
        ServerMode.Development,
        Php: new Dictionary<string, string>
        {
            ["display_errors"] = "On",
            ["display_startup_errors"] = "On",
            ["error_reporting"] = "E_ALL",
            ["log_errors"] = "On",
            ["max_execution_time"] = "300",
            ["max_input_time"] = "300",
            ["memory_limit"] = "1G",
            ["upload_max_filesize"] = "512M",
            ["post_max_size"] = "512M",
            ["opcache.validate_timestamps"] = "1",
            ["opcache.revalidate_freq"] = "0",
            ["expose_php"] = "On",
            ["disable_functions"] = "",
            ["session.use_strict_mode"] = "0",
            ["session.cookie_httponly"] = "0",
        },
        Apache: new Dictionary<string, string>
        {
            ["Timeout"] = "300",
            ["RequestReadTimeout"] = "header=0 body=0",
            ["LimitRequestBody"] = "536870912",
            ["ServerTokens"] = "Full",
            ["ServerSignature"] = "On",
            ["TraceEnable"] = "On",
            ["LogLevel"] = "info",
            ["PhpBackendTimeout"] = "300",
        },
        Database: new Dictionary<string, string>
        {
            ["statement_timeout"] = "0",
            ["wait_timeout"] = "28800",
            ["general_log"] = "ON",
            ["slow_query_log"] = "ON",
            ["long_query_time"] = "2",
        },
        Xdebug: true, SecurityHeaders: false, DirectoryListing: true);

    public static ModeProfile Production { get; } = new(
        ServerMode.Production,
        Php: new Dictionary<string, string>
        {
            ["display_errors"] = "Off",
            ["display_startup_errors"] = "Off",
            ["error_reporting"] = "E_ALL & ~E_DEPRECATED",
            ["log_errors"] = "On",
            ["max_execution_time"] = "30",
            ["max_input_time"] = "60",
            ["memory_limit"] = "256M",
            ["upload_max_filesize"] = "32M",
            ["post_max_size"] = "32M",
            ["opcache.validate_timestamps"] = "0",
            // Ignored while validate_timestamps is 0; present so both profiles carry the same keys.
            ["opcache.revalidate_freq"] = "0",
            ["expose_php"] = "Off",
            ["disable_functions"] = "exec,shell_exec,system,passthru,proc_open,popen",
            ["session.use_strict_mode"] = "1",
            ["session.cookie_httponly"] = "1",
        },
        Apache: new Dictionary<string, string>
        {
            ["Timeout"] = "60",
            ["RequestReadTimeout"] = "header=20-40,MinRate=500 body=20,MinRate=500",
            ["LimitRequestBody"] = "33554432",
            ["ServerTokens"] = "Prod",
            ["ServerSignature"] = "Off",
            ["TraceEnable"] = "Off",
            ["LogLevel"] = "warn",
            ["PhpBackendTimeout"] = "60",
        },
        Database: new Dictionary<string, string>
        {
            ["statement_timeout"] = "60",
            ["wait_timeout"] = "600",
            ["general_log"] = "OFF",
            ["slow_query_log"] = "ON",
            ["long_query_time"] = "5",
        },
        Xdebug: false, SecurityHeaders: true, DirectoryListing: false);

    public static ModeProfile For(ServerMode m) => m == ServerMode.Production ? Production : Development;
}
