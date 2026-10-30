namespace Anvil.Core.Settings;

/// <summary>
/// The fixed development values. PHP's go into php.ini once, when Anvil creates it, so the user's later edits win; Apache's
/// stay in the generated configuration.
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
        ["max_input_vars"] = "5000",
        ["memory_limit"] = "1G",
        ["upload_max_filesize"] = "256M",
        ["post_max_size"] = "256M",
        ["max_file_uploads"] = "50",
        ["realpath_cache_size"] = "16M",
        ["realpath_cache_ttl"] = "600",
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
}
