using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Anvil.Core.Certificates;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using Scriban;

namespace Anvil.Core.Apache;

/// <summary>
/// Renders data\conf: the global include, the development defaults, the PHP pools, the htdocs virtual hosts, one file per
/// network name and one per site.
/// </summary>
public sealed partial class ConfigRenderer(AppPaths paths)
{
    /// <summary>Loopback, RFC 1918, IPv6 unique-local and link-local.</summary>
    public const string PrivateRanges = "127.0.0.0/8 ::1 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 fc00::/7 fe80::/10";

    public RenderedConfig Render(RenderInput input)
    {
        var s = input.Settings;
        var defaults = DevelopmentDefaults.Apache;
        var htdocs = AppPaths.ForApache(s.HtdocsPath ?? paths.Htdocs);
        const string options = "Indexes FollowSymLinks";

        var modules = ApacheModules.Required.Concat(s.OptionalModules)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(m => !input.Stock.LoadedModules.Contains(m))
            .ToList();
        var sites = input.Sites.Where(x => x.Enabled && x.Kind != SiteKind.Htdocs).ToList();
        foreach (var site in sites) RefuseUnsafe(site);
        foreach (var entry in input.Htdocs) RefuseUnsafe(entry);
        var httpsCert = sites.Select(x => CertificateOf(x, input)).FirstOrDefault(c => c is not null);
        // Nothing listens on the HTTPS port without an HTTPS site: Apache's main server would answer there.
        var listen = (httpsCert is null ? new[] { s.HttpPort } : [s.HttpPort, s.HttpsPort])
            .Distinct().Where(p => !input.Stock.ListenPorts.Contains(p)).ToList();
        // htdocs runs on DefaultPhp; when that version has no pool, on the newest one that does.
        var htdocsPool = input.Pools.FirstOrDefault(p => string.Equals(p.Version, s.DefaultPhp, StringComparison.OrdinalIgnoreCase))
            ?? input.Pools.FirstOrDefault();
        // Without any pool a .php file is refused, never sent as its source; index.html stays the index.
        var htdocsPhp = PhpHandler(htdocsPool, "    ", withIndex: htdocsPool is not null);
        var networkEntries = HtdocsEntries(input, options, htdocsPool, local: false);
        var localEntries = HtdocsEntries(input, options, htdocsPool, local: true);

        var files = new Dictionary<string, string>
        {
            ["anvil.conf"] = Templates.Render("anvil.conf.sbn", new
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
                defaults_conf = AppPaths.ForApache(Path.Combine(paths.Conf, "defaults.conf")),
                php_pools_conf = AppPaths.ForApache(Path.Combine(paths.Conf, "php-pools.conf")),
                data = AppPaths.ForApache(paths.Data),
                vhosts = AppPaths.ForApache(paths.Vhosts),
            }),
            ["defaults.conf"] = Templates.Render("defaults.conf.sbn", new
            {
                timeout = defaults["Timeout"],
                request_read_timeout = defaults["RequestReadTimeout"],
                limit_request_body = defaults["LimitRequestBody"],
                server_tokens = defaults["ServerTokens"],
                server_signature = defaults["ServerSignature"],
                trace_enable = defaults["TraceEnable"],
                log_level = defaults["LogLevel"],
            }),
            ["php-pools.conf"] = Templates.Render("php-pools.conf.sbn", new
            {
                pools = input.Pools.Select(p => new { version = p.Version, port = p.Port }).ToList(),
                timeout = defaults["PhpBackendTimeout"],
            }),
            [Path.Combine("vhosts", "00-catchall.conf")] = Templates.Render("00-catchall.conf.sbn", new
            {
                port = s.HttpPort,
                htdocs,
                options,
                require = s.Network.HtdocsOnNetwork ? $"ip {PrivateRanges}" : "local",
                php_handler = htdocsPhp,
                entries = networkEntries.Blocks,
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
        // The first HTTPS virtual host answers every name no site claims, so it refuses them. It borrows a site's certificate only to finish the handshake; that name is public in the site's
        // certificate anyway.
        if (httpsCert is not null)
            files[Path.Combine("vhosts", "00-catchall-https.conf")] = Templates.Render("00-catchall-https.conf.sbn", new
            {
                port = s.HttpsPort,
                cert = CertFields(httpsCert),
            });
        // "net-" and "site-" keep every other file after 00-catchall, so the catch-all stays the default server whatever the
        // name. The bare network domain needs no file: the catch-all answers it.
        foreach (var name in NetworkNames.Plan(s.NetworkDomain, input.Sites, input.Htdocs, s.Network.HtdocsOnNetwork).Names)
        {
            files[Path.Combine("vhosts", $"net-{name.Label}.conf")] = name.SiteId is { } id
                ? RenderSite(sites.Single(x => x.Id == id), input, options, htdocsPool, name.Host)
                : RenderNetworkFolder(name, input, options, htdocsPool);
        }

        foreach (var site in sites)
            files[Path.Combine("vhosts", $"site-{site.Id}.conf")] = RenderSite(site, input, options, htdocsPool);
        return new RenderedConfig(files) { Folders = sites.Select(x => SiteLogDir(x.Id)).ToList() };
    }

    /// <summary>
    /// Sends existing .php files under one vhost or folder to <paramref name="pool"/>, indented to sit inside its block.
    /// With no pool (that PHP version is not running) .php files are refused rather than sent as source text.
    /// </summary>
    public static string PhpHandler(PhpPool? pool, string indent, bool withIndex = true) =>
        Indent(Templates.Render("_php-handler.sbn", new { port = pool?.Port, with_index = withIndex }), indent);

    /// <param name="networkHost">
    /// The site's name under the network domain: then only its plain HTTP virtual host is rendered, for that name alone,
    /// with no redirect to HTTPS (the certificate does not name it).
    /// </param>
    private string RenderSite(SiteDefinition site, RenderInput input, string options, PhpPool? defaultPool, string? networkHost = null)
    {
        var s = input.Settings;
        var serverName = networkHost ?? SiteSet.NormalizeHost(site.Name) ?? site.Name;
        var cert = networkHost is null ? CertificateOf(site, input) : null;
        var redirect = cert is not null && site.HttpRedirect ? $"https://{serverName}{(s.HttpsPort == 443 ? "" : $":{s.HttpsPort}")}/" : null;
        var vhosts = new List<object> { new { port = s.HttpPort, ssl = false, redirect = redirect is not null } };
        if (cert is not null) vhosts.Add(new { port = s.HttpsPort, ssl = true, redirect = false });

        var rules = AccessRules.Render(site);
        var index = site.Index.Count > 0 ? string.Join(' ', site.Index) : "index.php index.html";
        // An app's .htaccess may say "Require all granted", which replaces a <Directory>'s Require. A <Location> merges
        // after .htaccess files, and AuthMerging And keeps the site's restriction on top of them.
        return Templates.Render("vhost-site.conf.sbn", new
        {
            title = networkHost is null ? site.Name : $"{site.Name} on the local network: http://{networkHost}/",
            vhosts,
            server_name = serverName,
            aliases = networkHost is null && site.Aliases.Count > 0 ? string.Join(' ', site.Aliases.Select(a => SiteSet.NormalizeHost(a) ?? a)) : null,
            error_log = Rotated(SiteLogDir(site.Id), "error"),
            access_log = Rotated(SiteLogDir(site.Id), "access"),
            ssl_access_log = Rotated(SiteLogDir(site.Id), "ssl-access"),
            cert = cert is null ? null : CertFields(cert),
            redirect,
            proxy = site.Type == SiteType.Proxy ? Indent(ProxyBlock(site, rules), "    ") : null,
            root = AppPaths.ForApache(site.Root),
            options,
            index,
            rules = Indent(rules, "        "),
            php = site.Type == SiteType.Php ? PhpHandler(PoolFor(site.PhpVersion, input.Pools, defaultPool), "        ", withIndex: false) : null,
        });
    }

    /// <summary>An htdocs folder at its network name: the folder as the site root, on its own PHP version when it has one.</summary>
    private static string RenderNetworkFolder(NetworkName name, RenderInput input, string options, PhpPool? htdocsPool)
    {
        var entry = input.Htdocs.First(e => e.Folder == name.HtdocsFolder);
        return Templates.Render("vhost-network-htdocs.conf.sbn", new
        {
            folder = entry.Folder,
            host = name.Host,
            port = input.Settings.HttpPort,
            root = AppPaths.ForApache(entry.Root),
            options,
            require = $"ip {PrivateRanges}",
            php_handler = PhpHandler(PoolFor(entry.PhpVersion, input.Pools, htdocsPool), "    ", withIndex: htdocsPool is not null),
        });
    }

    /// <summary>
    /// The validator refuses these values on save; a site file edited by hand still reaches here, and a quote, line break or
    /// variable in it would rewrite the configuration around it.
    /// </summary>
    private static void RefuseUnsafe(SiteDefinition site)
    {
        var owner = $"site {site.Name}";
        Refuse(site.Name, nameof(SiteDefinition.Name), owner);
        Refuse(site.Root, nameof(SiteDefinition.Root), owner);
        foreach (var alias in site.Aliases) Refuse(alias, nameof(SiteDefinition.Aliases), owner);
        foreach (var index in site.Index) Refuse(index, nameof(SiteDefinition.Index), owner);
    }

    private static void RefuseUnsafe(HtdocsEntry entry)
    {
        var owner = $"htdocs folder {entry.Folder}";
        Refuse(entry.Folder, nameof(HtdocsEntry.Folder), owner);
        Refuse(entry.Root, nameof(HtdocsEntry.Root), owner);
    }

    private static void Refuse(string value, string field, string owner)
    {
        if (!SiteValidator.IsDirectiveSafe(value))
            throw new ArgumentException($"{field} of {owner} contains quotes, line breaks, %{{ or ${{.");
    }

    /// <summary>The certificate a site serves HTTPS with, or null while it serves HTTP only.</summary>
    private static SiteCertificate? CertificateOf(SiteDefinition site, RenderInput input) =>
        site.Cert == CertSource.LocalDev && input.Certificates.TryGetValue(site.Id, out var found) ? found : null;

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
            target = site.Root.TrimEnd('/') + "/",
            rules = Indent(rules, "    "),
        });
    }

    /// <summary>
    /// htdocs folders that need more than the htdocs defaults: aliases to folders elsewhere, their own PHP version, or
    /// (outside localhost) an access level other than the htdocs one. On localhost every folder stays local only.
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
            var pool = PoolFor(entry.PhpVersion, input.Pools, htdocsPool);
            var ownPhp = entry.PhpVersion is not null && pool != htdocsPool;
            if (!entry.IsAlias && !ownPhp && access == rootAccess) continue;

            var rules = AccessRules.Render(site with { Access = access });
            folders.Add(entry.Folder);
            blocks.Add(Indent(Templates.Render("_mount.sbn", new
            {
                alias = entry.IsAlias,
                path = "/" + entry.Folder,
                root = AppPaths.ForApache(entry.Root),
                options,
                index = "index.php index.html",
                rules = Indent(rules, "    "),
                php = ownPhp ? PhpHandler(pool, "    ", withIndex: false) : null,
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

    private string SiteLogDir(string id) => Path.Combine(paths.Logs, "sites", id);

    /// <summary>
    /// A log piped through Apache's rotatelogs: <c>name.YYYY-MM-DD.log</c>, a new file each local midnight. The value goes
    /// inside the directive's quotes. Apache opens one pipe for identical ErrorLog values but one per CustomLog line.
    /// </summary>
    private static string Rotated(string dir, string name) =>
        $$"""|\"${SRVROOT}/bin/rotatelogs.exe\" -l \"{{AppPaths.ForApache(Path.Combine(dir, name))}}.%Y-%m-%d.log\" 86400""";

    /// <summary>A site's own PHP version, or the htdocs pool when it uses the default. Null when that version has no pool.</summary>
    private static PhpPool? PoolFor(string? version, IReadOnlyList<PhpPool> pools, PhpPool? defaultPool) =>
        version is null ? defaultPool : pools.FirstOrDefault(p => string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase));

    private static string Indent(string text, string indent) =>
        string.Join(Environment.NewLine, text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Select(line => indent + line));

    private static class Templates
    {
        private static readonly ConcurrentDictionary<string, Template> Cache = new();

        public static string Render(string name, object model) =>
            Cache.GetOrAdd(name, Load).Render(model, member => member.Name);

        private static Template Load(string name)
        {
            using var stream = typeof(ConfigRenderer).Assembly.GetManifestResourceStream("Anvil.Templates." + name)
                ?? throw new InvalidOperationException($"Template {name} is not embedded.");
            using var reader = new StreamReader(stream);
            var template = Template.Parse(reader.ReadToEnd(), name);
            if (template.HasErrors)
                throw new InvalidOperationException($"Template {name}: {string.Join("; ", template.Messages)}");
            return template;
        }
    }
}
