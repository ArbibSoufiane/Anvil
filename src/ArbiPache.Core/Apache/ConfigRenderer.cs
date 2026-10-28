using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ArbiPache.Core.Certificates;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;
using Scriban;

namespace ArbiPache.Core.Apache;

/// <summary>Renders data\conf: the global include, the mode profile, the PHP pools, the htdocs virtual hosts and one file per site.</summary>
public sealed partial class ConfigRenderer(AppPaths paths)
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
        var sites = input.Sites.Where(x => x.Enabled && x.Kind != SiteKind.Htdocs).ToList();
        var catchAllSite = sites.FirstOrDefault(x => x.Kind == SiteKind.Ip && x.Name == "*");
        var httpsCert = sites.Select(x => CertificateOf(x, input)).FirstOrDefault(c => c is not null);
        // Nothing listens on the HTTPS port without an HTTPS site: Apache's main server would answer there.
        var listen = (httpsCert is null ? new[] { s.HttpPort } : [s.HttpPort, s.HttpsPort])
            .Distinct().Where(p => !input.Stock.ListenPorts.Contains(p)).ToList();
        // htdocs runs on DefaultPhp; when that version has no pool, on the newest one that does.
        var htdocsPool = input.Pools.FirstOrDefault(p => string.Equals(p.Version, s.DefaultPhp, StringComparison.OrdinalIgnoreCase))
            ?? input.Pools.FirstOrDefault();
        var htdocsPhp = htdocsPool is null ? null : PhpHandler(htdocsPool, "    ");
        var networkEntries = HtdocsEntries(input, options, htdocsPool, local: false);
        var localEntries = HtdocsEntries(input, options, htdocsPool, local: true);

        var files = new Dictionary<string, string>
        {
            ["arbipache.conf"] = Templates.Render("arbipache.conf.sbn", new
            {
                version = BuildInfo.Version,
                modules,
                listen,
                pid_file = AppPaths.ForApache(Path.Combine(paths.Run, "httpd.pid")),
                error_log = Rotated(Path.Combine(paths.Logs, "apache"), "error"),
                access_log = Rotated(Path.Combine(paths.Logs, "apache"), "access"),
                threads_per_child = s.Apache.ThreadsPerChild,
                max_connections_per_child = s.Apache.MaxConnectionsPerChild,
                keep_alive_timeout = s.Apache.KeepAliveTimeout,
                ssl_session_cache = AppPaths.ForApache(Path.Combine(paths.Run, "ssl_scache")),
                http2 = s.OptionalModules.Contains("http2", StringComparer.OrdinalIgnoreCase),
                mode_conf = AppPaths.ForApache(Path.Combine(paths.Conf, "mode.conf")),
                php_pools_conf = AppPaths.ForApache(Path.Combine(paths.Conf, "php-pools.conf")),
                acme_challenges = AppPaths.ForApache(Certificates.AcmeService.ChallengeDir(paths)),
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
            [Path.Combine("vhosts", "00-catchall.conf")] = catchAllSite is not null
                ? RenderSite(catchAllSite, input, options, htdocsPool, catchAll: true)
                : Templates.Render("00-catchall.conf.sbn", new
                {
                    port = s.HttpPort,
                    deny_all = profile.Mode == ServerMode.Production,
                    htdocs,
                    options,
                    require = s.Network.HtdocsOnNetwork ? $"ip {PrivateRanges}" : "local",
                    php_handler = profile.Mode == ServerMode.Production ? null : htdocsPhp,
                    entries = profile.Mode == ServerMode.Production ? null : networkEntries.Blocks,
                    root_guard = RootGuard(networkEntries.Folders),
                }),
            [Path.Combine("vhosts", "01-localhost.conf")] = Templates.Render("01-localhost.conf.sbn", new
            {
                port = s.HttpPort,
                htdocs,
                options,
                php_handler = htdocsPhp,
                entries = localEntries.Blocks,
                root_guard = RootGuard(localEntries.Folders),
            }),
        };
        // The first HTTPS virtual host answers every name no site claims, so it refuses them, as the HTTP catch-all does in
        // Production. It borrows a site's certificate only to finish the handshake; that name is public in the site's
        // certificate anyway. An IP site for all addresses with its own certificate is that first host already.
        if (httpsCert is not null && (catchAllSite is null || CertificateOf(catchAllSite, input) is null))
            files[Path.Combine("vhosts", "00-catchall-https.conf")] = Templates.Render("00-catchall-https.conf.sbn", new
            {
                port = s.HttpsPort,
                cert = CertFields(httpsCert),
            });
        // "site-" keeps every site file after 00-catchall, so the catch-all stays the default server whatever the domain.
        foreach (var site in sites.Where(x => x != catchAllSite))
            files[Path.Combine("vhosts", $"site-{site.Id}.conf")] = RenderSite(site, input, options, htdocsPool, catchAll: false);
        return new RenderedConfig(files) { Folders = sites.Select(x => SiteLogDir(x.Id)).ToList() };
    }

    /// <summary>
    /// Sends existing .php files under one vhost or folder to <paramref name="pool"/>, indented to sit inside its block.
    /// With no pool (that PHP version is not running) .php files are refused rather than sent as source text.
    /// </summary>
    public static string PhpHandler(PhpPool? pool, string indent, bool withIndex = true) =>
        Indent(Templates.Render("_php-handler.sbn", new { port = pool?.Port, with_index = withIndex }), indent);

    private string RenderSite(SiteDefinition site, RenderInput input, string options, PhpPool? defaultPool, bool catchAll)
    {
        var s = input.Settings;
        // Apache reads "[" in ServerName as a wildcard and compares IPv6 hosts without brackets: an IPv6 site answers by alias.
        var v6 = site.Kind == SiteKind.Ip && IPAddress.TryParse(site.Name, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? ip.ToString()
            : null;
        var serverName = catchAll ? CatchAllName
            : v6 is not null ? $"{site.Id}.invalid"
            : site.Kind == SiteKind.Domain ? SiteSet.NormalizeHost(site.Name) ?? site.Name
            : site.Name;
        var cert = CertificateOf(site, input);
        // The catch-all answers unknown names, so it has no name to redirect to.
        var redirect = cert is not null && site.HttpRedirect && !catchAll
            ? $"https://{(v6 is not null ? $"[{v6}]" : serverName)}{(s.HttpsPort == 443 ? "" : $":{s.HttpsPort}")}/"
            : null;
        var vhosts = new List<object> { new { port = s.HttpPort, ssl = false, redirect = redirect is not null } };
        if (cert is not null) vhosts.Add(new { port = s.HttpsPort, ssl = true, redirect = false });

        var rules = AccessRules.Render(site, site.PasswordProtected ? HtpasswdPath(site.Id) : null);
        var index = site.Index.Count > 0 ? string.Join(' ', site.Index) : "index.php index.html";
        // An app's .htaccess may say "Require all granted", which replaces a <Directory>'s Require. A <Location> merges
        // after .htaccess files, and AuthMerging And keeps the site's restriction on top of them.
        var guard = rules != "Require all granted";
        return Templates.Render("vhost-site.conf.sbn", new
        {
            title = catchAll ? "all addresses (first virtual host, answers every name no other site claims)" : site.Name,
            vhosts,
            server_name = serverName,
            aliases = v6 is not null && !catchAll ? v6
                : site.Kind == SiteKind.Domain && site.Aliases.Count > 0 ? string.Join(' ', site.Aliases.Select(a => SiteSet.NormalizeHost(a) ?? a))
                : null,
            error_log = Rotated(SiteLogDir(site.Id), "error"),
            access_log = Rotated(SiteLogDir(site.Id), "access"),
            ssl_access_log = Rotated(SiteLogDir(site.Id), "ssl-access"),
            cert = cert is null ? null : CertFields(cert),
            redirect,
            lb = site.Cert == CertSource.LoadBalancer && site.TrustedProxies.Count > 0
                ? Indent(Templates.Render("_lb.sbn", new { proxies = site.TrustedProxies, match = ProxyMatch(site.TrustedProxies) }), "    ")
                : null,
            proxy = site.Type == SiteType.Proxy ? Indent(ProxyBlock(site, rules), "    ") : null,
            root = AppPaths.ForApache(site.Root),
            options,
            index,
            rules = Indent(rules, "        "),
            guard,
            php = site.Type == SiteType.Php ? PhpHandler(PoolFor(site.PhpVersion, input.Pools, defaultPool), "        ", withIndex: false) : null,
            mounts = site.Mounts.Select(m => Indent(Templates.Render("_mount.sbn", new
            {
                proxy = m.Type == SiteType.Proxy ? m.Root.TrimEnd('/') + "/" : null,
                alias = true,
                path = m.UrlPath,
                root = AppPaths.ForApache(m.Root),
                options,
                index,
                rules = Indent(rules, "    "),
                php = m.Type == SiteType.Php ? PhpHandler(PoolFor(m.PhpVersion ?? site.PhpVersion, input.Pools, defaultPool), "    ", withIndex: false) : null,
                fallback = m.Type == SiteType.Php,
                guard = (string?)null, // the site's <Location "/"> covers its mounts: they share its rules
            }), "    ")).ToList(),
        });
    }

    /// <summary>The certificate a site serves HTTPS with, or null while it serves HTTP only.</summary>
    private static SiteCertificate? CertificateOf(SiteDefinition site, RenderInput input) =>
        site.Cert is CertSource.LetsEncrypt or CertSource.Imported or CertSource.LocalDev
        && input.Certificates.TryGetValue(site.Id, out var found) ? found : null;

    private static object CertFields(SiteCertificate cert) => new
    {
        cert = AppPaths.ForApache(cert.CertFile),
        key = AppPaths.ForApache(cert.KeyFile),
        chain = cert.ChainFile is null ? null : AppPaths.ForApache(cert.ChainFile),
    };

    private static string ProxyBlock(SiteDefinition site, string rules)
    {
        var target = new Uri(site.Root);
        return Templates.Render("_proxy.sbn", new
        {
            ws = $"{(target.Scheme == Uri.UriSchemeHttps ? "wss" : "ws")}://{target.Host}:{target.Port}",
            excluded = site.Mounts.Where(m => m.Type != SiteType.Proxy).Select(m => m.UrlPath).ToList(),
            target = site.Root.TrimEnd('/') + "/",
            rules = Indent(rules, "    "),
        });
    }

    /// <summary>
    /// htdocs folders that need more than the htdocs defaults: aliases to folders elsewhere, their own PHP version, a password,
    /// or (outside localhost) an access level other than the htdocs one. On localhost every folder stays local only.
    /// </summary>
    private (string? Blocks, IReadOnlyList<string> Folders) HtdocsEntries(RenderInput input, string options, PhpPool? htdocsPool, bool local)
    {
        var folders = new List<string>();
        var rootAccess = local ? AccessLevel.ThisPc : input.Settings.Network.HtdocsOnNetwork ? AccessLevel.Network : AccessLevel.ThisPc;
        var blocks = new List<string>();
        foreach (var entry in input.Htdocs)
        {
            var site = input.Sites.FirstOrDefault(x => x.Kind == SiteKind.Htdocs && string.Equals(x.Name, entry.Folder, StringComparison.OrdinalIgnoreCase))
                ?? new SiteDefinition
                {
                    Id = SiteSet.IdFor(SiteKind.Htdocs, entry.Folder), Kind = SiteKind.Htdocs, Type = SiteType.Php, Name = entry.Folder, Root = entry.Root,
                };
            // "My network" never opens a folder wider than the htdocs network setting does.
            var access = local ? AccessLevel.ThisPc : entry.Access == AccessLevel.Network ? rootAccess : entry.Access;
            if (access == AccessLevel.SpecificIps && site.AllowedIps.Count == 0) access = AccessLevel.ThisPc;
            var pool = PoolFor(entry.PhpVersion, input.Pools, htdocsPool);
            var ownPhp = entry.PhpVersion is not null && pool != htdocsPool;
            if (!entry.IsAlias && !ownPhp && !site.PasswordProtected && access == rootAccess) continue;

            var rules = AccessRules.Render(site with { Access = access, AcceptOnlyFromProxies = false }, site.PasswordProtected ? HtpasswdPath(site.Id) : null);
            folders.Add(entry.Folder);
            blocks.Add(Indent(Templates.Render("_mount.sbn", new
            {
                proxy = (string?)null,
                alias = entry.IsAlias,
                path = "/" + entry.Folder,
                root = AppPaths.ForApache(entry.Root),
                options,
                index = "index.php index.html",
                rules = Indent(rules, "    "),
                php = ownPhp ? PhpHandler(pool, "    ", withIndex: false) : null,
                fallback = false,
                guard = $"(?i)^/{PcreEscape(entry.Folder)}(?:/|$)",
            }), "    "));
        }

        return (blocks.Count == 0 ? null : string.Join(Environment.NewLine, blocks), folders);
    }

    /// <summary>
    /// The URLs the htdocs root's own rule guards: all but the folders with rules of their own, which guard themselves.
    /// Case-insensitive, as Windows maps /PMA/ to the pma folder.
    /// </summary>
    private static string RootGuard(IReadOnlyList<string> folders) =>
        folders.Count == 0 ? "(?i)^/" : $"(?i)^/(?!(?:{string.Join('|', folders.Select(PcreEscape))})(?:/|$))";

    private static string PcreEscape(string text) => PcreSpecial().Replace(text, @"\$0");

    [GeneratedRegex(@"[\\.^$|?*+()\[\]{}]")]
    private static partial Regex PcreSpecial();

    private const string CatchAllName = "arbipache-catchall.invalid";

    private string SiteLogDir(string id) => Path.Combine(paths.Logs, "sites", id);

    /// <summary>
    /// A log piped through Apache's rotatelogs: <c>name.YYYY-MM-DD.log</c>, a new file each local midnight. The value goes
    /// inside the directive's quotes. Apache opens one pipe for identical ErrorLog values but one per CustomLog line.
    /// </summary>
    private static string Rotated(string dir, string name) =>
        $$"""|\"${SRVROOT}/bin/rotatelogs.exe\" -l \"{{AppPaths.ForApache(Path.Combine(dir, name))}}.%Y-%m-%d.log\" 86400""";

    private string HtpasswdPath(string id) => Path.Combine(paths.Auth, id + ".htpasswd");

    /// <summary>A site's own PHP version, or the htdocs pool when it uses the default. Null when that version has no pool.</summary>
    private static PhpPool? PoolFor(string? version, IReadOnlyList<PhpPool> pools, PhpPool? defaultPool) =>
        version is null ? defaultPool : pools.FirstOrDefault(p => string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase));

    private static string ProxyMatch(IEnumerable<string> proxies) =>
        string.Join(" || ", proxies.Select(p => $"%{{CONN_REMOTE_ADDR}} -ipmatch '{p}'"));

    private static string Indent(string text, string indent) =>
        string.Join(Environment.NewLine, text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Select(line => indent + line));

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
