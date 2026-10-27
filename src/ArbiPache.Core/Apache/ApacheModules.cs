namespace ArbiPache.Core.Apache;

/// <summary>The modules ArbiPache loads. Required ones carry features the generated configuration depends on.</summary>
public static class ApacheModules
{
    public static IReadOnlyList<string> Required { get; } =
    [
        "ssl", "socache_shmcb", "rewrite", "headers", "remoteip", "reqtimeout", "proxy", "proxy_http", "proxy_wstunnel",
        "proxy_fcgi", "proxy_balancer", "lbmethod_bybusyness", "slotmem_shm", "autoindex", "auth_basic", "authn_file",
        "authn_core", "authz_user", "alias",
    ];

    /// <summary>Modules the user may switch on or off on the Apache page.</summary>
    public static IReadOnlyList<string> Optional { get; } = ["deflate", "brotli", "expires", "http2", "status", "info"];
}
