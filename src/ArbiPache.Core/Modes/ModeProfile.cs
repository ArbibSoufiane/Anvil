using ArbiPache.Core.Settings;

namespace ArbiPache.Core.Modes;

/// <param name="Php">php.ini key to value.</param>
/// <param name="Apache">Directive to value: Timeout, RequestReadTimeout, LimitRequestBody, ServerTokens,
/// ServerSignature, TraceEnable, LogLevel, PhpBackendTimeout.</param>
/// <param name="Database">Engine-neutral keys: statement_timeout (seconds), wait_timeout, general_log,
/// slow_query_log, long_query_time.</param>
public sealed record ModeProfile(ServerMode Mode,
    IReadOnlyDictionary<string, string> Php,
    IReadOnlyDictionary<string, string> Apache,
    IReadOnlyDictionary<string, string> Database,
    bool Xdebug, bool SecurityHeaders, bool DirectoryListing);
