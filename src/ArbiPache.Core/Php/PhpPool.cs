namespace ArbiPache.Core.Php;

/// <summary>One PHP version's worker pool: <paramref name="Workers"/> php-cgi processes sharing one socket on <paramref name="Port"/>.</summary>
/// <param name="Key">Names the pool in config and process names, e.g. "php85".</param>
/// <param name="IniDir">PHPRC: the folder holding the version's php.ini.</param>
/// <param name="ScanDir">PHP_INI_SCAN_DIR: extra .ini files loaded after php.ini.</param>
public sealed record PhpPool(string Version, string Key, int Port, int Workers, int MaxRequests, string PhpCgi, string IniDir, string ScanDir);
