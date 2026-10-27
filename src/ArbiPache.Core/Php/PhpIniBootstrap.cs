using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Php;

/// <summary>
/// Creates data\php\&lt;v&gt;\php.ini from the version's php.ini-development once. Later calls keep the
/// user's edits and only re-point the paths ArbiPache manages, so the folder stays portable.
/// </summary>
public sealed class PhpIniBootstrap(AppPaths paths)
{
    public static readonly IReadOnlyList<string> DefaultExtensions =
        ["curl", "exif", "fileinfo", "gd", "intl", "mbstring", "mysqli", "openssl", "pdo_mysql", "sodium", "zip"];

    public void EnsureIni(string version)
    {
        var dir = paths.PhpConfigDir(version);
        var file = Path.Combine(dir, "php.ini");
        Directory.CreateDirectory(Path.Combine(dir, "conf.d"));
        var caBundle = CaBundle();

        var created = !File.Exists(file);
        var doc = IniDocument.Parse(created ? Template(version) : File.ReadAllText(file));
        var before = doc.ToString();

        // PHP reads \\ and \$ inside double quotes as escapes; forward slashes avoid both.
        var runtime = paths.PhpRuntime(version);
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

        if (created)
        {
            foreach (var extension in DefaultExtensions) doc.AddValue("PHP", "extension", extension);
            if (!PhpSupport.HasBuiltInOpcache(version)) doc.AddValue("PHP", "zend_extension", "opcache");
        }

        var text = doc.ToString();
        if (created || text != before) AtomicFile.WriteAllText(file, text);
    }

    private string Template(string version)
    {
        var template = Path.Combine(paths.PhpRuntime(version), "php.ini-development");
        return File.Exists(template) ? File.ReadAllText(template) : "[PHP]\r\n";
    }

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
