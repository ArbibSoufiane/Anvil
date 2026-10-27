using System.Collections.Concurrent;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;
using ArbiPache.Core.Settings;
using Scriban;

namespace ArbiPache.Core.Apache;

/// <summary>Renders data\conf: the global include, the mode profile, the PHP pools and the default htdocs virtual hosts.</summary>
public sealed class ConfigRenderer(AppPaths paths)
{
    /// <summary>Loopback, RFC 1918, IPv6 unique-local and link-local.</summary>
    public const string PrivateRanges = "127.0.0.0/8 ::1 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 fc00::/7 fe80::/10";

    public RenderedConfig Render(RenderInput input)
    {
        var s = input.Settings;
        var profile = input.Profile;
        var htdocs = AppPaths.ForApache(s.HtdocsPath ?? paths.Htdocs);
        var options = profile.DirectoryListing ? "Indexes FollowSymLinks" : "FollowSymLinks";

        var modules = ApacheModules.Required.Concat(s.OptionalModules)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(m => !input.Stock.LoadedModules.Contains(m))
            .ToList();
        var listen = new[] { s.HttpPort, s.HttpsPort }.Distinct().Where(p => !input.Stock.ListenPorts.Contains(p)).ToList();
        // htdocs runs on DefaultPhp; when that version has no pool, on the newest one that does.
        var htdocsPool = input.Pools.FirstOrDefault(p => string.Equals(p.Version, s.DefaultPhp, StringComparison.OrdinalIgnoreCase))
            ?? input.Pools.FirstOrDefault();
        var htdocsPhp = htdocsPool is null ? null : PhpHandler(htdocsPool, "    ");

        var files = new Dictionary<string, string>
        {
            ["arbipache.conf"] = Templates.Render("arbipache.conf.sbn", new
            {
                version = BuildInfo.Version,
                modules,
                listen,
                pid_file = AppPaths.ForApache(Path.Combine(paths.Run, "httpd.pid")),
                error_log = AppPaths.ForApache(Path.Combine(paths.Logs, "apache", "error.log")),
                threads_per_child = s.Apache.ThreadsPerChild,
                max_connections_per_child = s.Apache.MaxConnectionsPerChild,
                keep_alive_timeout = s.Apache.KeepAliveTimeout,
                ssl_session_cache = AppPaths.ForApache(Path.Combine(paths.Run, "ssl_scache")),
                http2 = s.OptionalModules.Contains("http2", StringComparer.OrdinalIgnoreCase),
                mode_conf = AppPaths.ForApache(Path.Combine(paths.Conf, "mode.conf")),
                php_pools_conf = AppPaths.ForApache(Path.Combine(paths.Conf, "php-pools.conf")),
                vhosts = AppPaths.ForApache(paths.Vhosts),
            }),
            ["mode.conf"] = Templates.Render("mode.conf.sbn", new
            {
                mode = profile.Mode.ToString(),
                timeout = profile.Apache["Timeout"],
                request_read_timeout = profile.Apache["RequestReadTimeout"],
                limit_request_body = profile.Apache["LimitRequestBody"],
                server_tokens = profile.Apache["ServerTokens"],
                server_signature = profile.Apache["ServerSignature"],
                trace_enable = profile.Apache["TraceEnable"],
                log_level = profile.Apache["LogLevel"],
                security_headers = profile.SecurityHeaders,
            }),
            ["php-pools.conf"] = Templates.Render("php-pools.conf.sbn", new
            {
                pools = input.Pools.Select(p => new { version = p.Version, port = p.Port }).ToList(),
                timeout = profile.Apache["PhpBackendTimeout"],
            }),
            [Path.Combine("vhosts", "00-catchall.conf")] = Templates.Render("00-catchall.conf.sbn", new
            {
                port = s.HttpPort,
                deny_all = profile.Mode == ServerMode.Production,
                htdocs,
                options,
                require = s.Network.HtdocsOnNetwork ? $"ip {PrivateRanges}" : "local",
                php_handler = profile.Mode == ServerMode.Production ? null : htdocsPhp,
            }),
            [Path.Combine("vhosts", "01-localhost.conf")] = Templates.Render("01-localhost.conf.sbn", new
            {
                port = s.HttpPort,
                htdocs,
                options,
                php_handler = htdocsPhp,
            }),
        };
        return new RenderedConfig(files);
    }

    /// <summary>Sends existing .php files under one vhost or mount to <paramref name="pool"/>, indented to sit inside its block.</summary>
    public static string PhpHandler(PhpPool pool, string indent) =>
        string.Join('\n', Templates.Render("_php-handler.sbn", new { port = pool.Port })
            .TrimEnd('\n').Split('\n').Select(line => indent + line));

    private static class Templates
    {
        private static readonly ConcurrentDictionary<string, Template> Cache = new();

        public static string Render(string name, object model) =>
            Cache.GetOrAdd(name, Load).Render(model, member => member.Name);

        private static Template Load(string name)
        {
            using var stream = typeof(ConfigRenderer).Assembly.GetManifestResourceStream("ArbiPache.Templates." + name)
                ?? throw new InvalidOperationException($"Template {name} is not embedded.");
            using var reader = new StreamReader(stream);
            var template = Template.Parse(reader.ReadToEnd(), name);
            if (template.HasErrors)
                throw new InvalidOperationException($"Template {name}: {string.Join("; ", template.Messages)}");
            return template;
        }
    }
}
