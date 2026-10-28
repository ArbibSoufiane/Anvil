using Anvil.Core.Database;

namespace Anvil.Core.Settings;

/// <summary>
/// The fixed development values. PHP's and the database's go into php.ini and my.ini once, when Anvil creates them, so the
/// user's later edits win; Apache's stay in the generated configuration.
/// </summary>
public static class DevelopmentDefaults
{
    public static IReadOnlyDictionary<string, string> Php { get; } = new Dictionary<string, string>
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
    };

    /// <summary>Timeout, RequestReadTimeout, LimitRequestBody, ServerTokens, ServerSignature, TraceEnable, LogLevel, PhpBackendTimeout.</summary>
    public static IReadOnlyDictionary<string, string> Apache { get; } = new Dictionary<string, string>
    {
        ["Timeout"] = "300",
        ["RequestReadTimeout"] = "header=0 body=0",
        ["LimitRequestBody"] = "536870912",
        ["ServerTokens"] = "Full",
        ["ServerSignature"] = "On",
        ["TraceEnable"] = "On",
        ["LogLevel"] = "info",
        ["PhpBackendTimeout"] = "300",
    };

    /// <summary>
    /// The [mysqld] options as the engine names them. No statement time limit: MariaDB's max_statement_time is in
    /// seconds, MySQL's max_execution_time in milliseconds, and 0 means none for both.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Database(DbEngine e) =>
    [
        new(e == DbEngine.MariaDb ? "max_statement_time" : "max_execution_time", "0"),
        new("wait_timeout", "28800"),
        new("general_log", "ON"),
        new("slow_query_log", "ON"),
        new("long_query_time", "2"),
    ];
}
