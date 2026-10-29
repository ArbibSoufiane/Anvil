using Anvil.Core.IO;
using Anvil.Core.Paths;
using Anvil.Core.Settings;

namespace Anvil.Core.Php;

/// <summary>
/// Creates data\php\&lt;v&gt;\php.ini from the version's php.ini-development once, with the development values. Later
/// calls keep the user's edits and only re-point the paths Anvil manages, so the folder stays portable.
/// </summary>
/// <param name="phpFolder">Where a version's files are: runtime\php\&lt;v&gt; unless it is a linked folder such as pvm's.</param>
public sealed class PhpIniBootstrap(AppPaths paths, Func<string, string>? phpFolder = null)
{
    public static readonly IReadOnlyList<string> DefaultExtensions =
        ["curl", "exif", "fileinfo", "gd", "intl", "mbstring", "mysqli", "openssl", "pdo_mysql", "sodium", "zip"];

    // Older builds wrote the mode's values into conf.d, loaded after php.ini.
    private static readonly (string File, string Marker)[] OldOverlays =
    [
        ("99-arbipache-mode.ini", "; Written by ArbiPache"), // legacy-name
        ("99-anvil-mode.ini", "; Written by Anvil"),
    ];

    public void EnsureIni(string version)
    {
        var dir = paths.PhpConfigDir(version);
        var file = Path.Combine(dir, "php.ini");
        Directory.CreateDirectory(Path.Combine(dir, "conf.d"));
        var caBundle = CaBundle();

        var created = !File.Exists(file);
        var doc = IniDocument.Parse(created ? Template(version) : File.ReadAllText(file));
        var before = doc.ToString();
        // The overlay's values were the ones PHP ran with, so they move into php.ini.
        var overlayRemoved = DeleteOldOverlays(dir);

        // PHP reads \\ and \$ inside double quotes as escapes; forward slashes avoid both.
        var runtime = Folder(version);
        var logs = Path.Combine(paths.Logs, "php");
        var sessions = Path.Combine(paths.Tmp, "php-sessions", version);
        var uploads = Path.Combine(paths.Tmp, "php-uploads", version);
        var temp = Path.Combine(paths.Tmp, "php", version);
        foreach (var d in new[] { logs, sessions, uploads, temp }) Directory.CreateDirectory(d);

        doc.Set("PHP", "extension_dir", Quote(Path.Combine(runtime, "ext")));
        doc.Set("PHP", "error_log", Quote(Path.Combine(logs, version + ".log")));
        doc.Set("PHP", "upload_tmp_dir", Quote(uploads));
        doc.Set("PHP", "sys_temp_dir", Quote(temp));
        doc.Set("Session", "session.save_path", Quote(sessions));
        doc.Set("curl", "curl.cainfo", Quote(caBundle));
        doc.Set("openssl", "openssl.cafile", Quote(caBundle));

        if (created || overlayRemoved)
            foreach (var (key, value) in DevelopmentDefaults.Php) doc.Set("PHP", key, value);
        if (created)
        {
            foreach (var extension in DefaultExtensions) doc.AddValue("PHP", "extension", extension);
            if (!PhpSupport.HasBuiltInOpcache(version)) doc.AddValue("PHP", "zend_extension", "opcache");
        }

        var text = doc.ToString();
        if (created || text != before) AtomicFile.WriteAllText(file, text);
    }

    /// <summary>Deletes the overlay files an older build generated; a file of the same name the user wrote stays.</summary>
    private static bool DeleteOldOverlays(string dir)
    {
        var removed = false;
        foreach (var (name, marker) in OldOverlays)
        {
            var file = Path.Combine(dir, "conf.d", name);
            if (!File.Exists(file) || !(File.ReadLines(file).FirstOrDefault() ?? "").StartsWith(marker, StringComparison.Ordinal)) continue;
            File.Delete(file);
            removed = true;
        }
        return removed;
    }

    private string Template(string version)
    {
        var template = Path.Combine(Folder(version), "php.ini-development");
        return File.Exists(template) ? File.ReadAllText(template) : "[PHP]\r\n";
    }

    private string Folder(string version) => phpFolder?.Invoke(version) ?? paths.PhpRuntime(version);

    private string CaBundle()
    {
        var root = Path.Combine(paths.Runtime, "cabundle");
        // Versions are ISO dates, so the newest sorts last.
        var newest = Directory.Exists(root)
            ? Directory.GetDirectories(root).Order(StringComparer.Ordinal).Select(d => Path.Combine(d, "cacert.pem")).LastOrDefault(File.Exists)
            : null;
        return newest ?? throw new InvalidOperationException("The CA bundle is not installed. Install it before PHP so curl and openssl can verify HTTPS.");
    }

    private static string Quote(string path) => $"\"{AppPaths.ForApache(path)}\"";
}
