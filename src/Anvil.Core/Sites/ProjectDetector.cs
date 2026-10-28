using System.Text.Json;

namespace Anvil.Core.Sites;

/// <summary>What the add-site form pre-fills for a folder: site type, the folder to serve, a label, and a proxy target.</summary>
public sealed record Detected(SiteType Type, string ServeRoot, string Label, string? ProxyTarget);

/// <summary>Guesses the project type from marker files at the top of a folder. Never reads below the top level.</summary>
public static class ProjectDetector
{
    public const string ViteTarget = "http://127.0.0.1:5173";

    public static Detected Detect(string folder)
    {
        bool Has(string relative) => File.Exists(Path.Combine(folder, relative));
        var publicDir = Path.Combine(folder, "public");
        var publicIndex = Has(@"public\index.php");

        if (Has("artisan") && publicIndex) return new Detected(SiteType.Php, publicDir, "Laravel", null);
        if (Has(@"bin\console") && publicIndex) return new Detected(SiteType.Php, publicDir, "Symfony", null);
        if (Has("wp-config.php") || Has("wp-config-sample.php")) return new Detected(SiteType.Php, folder, "WordPress", null);
        if (Has("composer.json")) return new Detected(SiteType.Php, publicIndex ? publicDir : folder, "PHP", null);
        if (UsesVite(Path.Combine(folder, "package.json"))) return new Detected(SiteType.Proxy, folder, "Vite", ViteTarget);
        if (OnlyHtml(folder)) return new Detected(SiteType.Static, folder, "Static", null);
        return new Detected(SiteType.Php, folder, "PHP", null);
    }

    private static bool UsesVite(string packageJson)
    {
        if (!File.Exists(packageJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && new[] { "dependencies", "devDependencies" }.Any(key =>
                    doc.RootElement.TryGetProperty(key, out var deps) && deps.ValueKind == JsonValueKind.Object && deps.TryGetProperty("vite", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool OnlyHtml(string folder)
    {
        if (!Directory.Exists(folder)) return false;
        var files = Directory.GetFiles(folder);
        return files.Any(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            && !files.Any(f => f.EndsWith(".php", StringComparison.OrdinalIgnoreCase));
    }
}
