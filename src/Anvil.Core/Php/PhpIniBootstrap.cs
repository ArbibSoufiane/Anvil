using Anvil.Core.IO;
using Anvil.Core.Paths;
using Anvil.Core.Settings;

namespace Anvil.Core.Php;

/// <summary>
/// Creates data\php\&lt;v&gt;\php.ini once, with the development values: from the folder's own php.ini when a PHP folder of the
/// user's (such as pvm's) has one, else from its php.ini-development. Later calls keep the user's edits and only re-point
/// the paths Anvil manages, so the folder stays portable. The extensions a folder's own php.ini loads are carried over
/// once, also into a php.ini Anvil made before.
/// </summary>
/// <param name="phpFolder">Where a version's files are: runtime\php\&lt;v&gt; unless it is a linked folder such as pvm's.</param>
public sealed class PhpIniBootstrap(AppPaths paths, Func<string, string>? phpFolder = null)
{
    public static readonly IReadOnlyList<string> DefaultExtensions =
        ["curl", "exif", "fileinfo", "gd", "intl", "mbstring", "mysqli", "openssl", "pdo_mysql", "sodium", "zip"];

    /// <summary>Starts the comment line that records whose extensions a php.ini took over.</summary>
    public const string MirrorMark = "; Anvil took the extensions of ";

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
        var own = OwnIni(version);
        var doc = IniDocument.Parse(created ? own is not null ? File.ReadAllText(own) : Template(version) : File.ReadAllText(file));
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
        if (created && own is null)
        {
            foreach (var extension in DefaultExtensions) doc.AddValue("PHP", "extension", extension);
            if (!PhpSupport.HasBuiltInOpcache(version)) doc.AddValue("PHP", "zend_extension", "opcache");
        }

        var mirror = own is not null && !before.Contains(MirrorMark, StringComparison.Ordinal);
        if (mirror) MirrorExtensions(doc, IniDocument.Parse(File.ReadAllText(own!)), runtime);

        var text = doc.ToString();
        if (mirror) text = $"{MirrorMark}{own}{(text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n")}{text}";
        if (created || text != before) AtomicFile.WriteAllText(file, text);
    }

    /// <summary>
    /// Loads every extension the folder's own php.ini loads and Anvil's does not. A name found in the folder's ext loads by
    /// name; one found only in the extension_dir the folder's php.ini names, or given by its full path, loads by full path.
    /// One whose DLL is nowhere is left out.
    /// </summary>
    private static void MirrorExtensions(IniDocument doc, IniDocument own, string folder)
    {
        var ext = Path.Combine(folder, "ext");
        var ownExt = own.Get("PHP", "extension_dir") is { Length: > 0 } dir ? Path.GetFullPath(dir, folder) : ext;
        foreach (var section in own.Sections().Prepend(""))
            foreach (var key in new[] { "extension", "zend_extension" })
                foreach (var value in own.GetAll(section, key))
                {
                    if (Resolve(value, ext, ownExt) is not { } resolved) continue;
                    var (load, byPath) = resolved;
                    // A name the folder's extension_dir resolved does not load from ext, where Anvil points PHP.
                    if (byPath && !Path.IsPathFullyQualified(value)) doc.RemoveValue("PHP", key, value);
                    if (PhpExtensionManager.Loads(doc, PhpExtensionManager.NameOf(value))) continue;
                    doc.AddValue("PHP", key, load);
                }
    }

    private static (string Load, bool ByPath)? Resolve(string value, string ext, string ownExt)
    {
        if (Path.IsPathFullyQualified(value)) return File.Exists(value) ? (Quote(value), true) : null;
        string[] files = [value, value + ".dll", $"php_{value}.dll"];
        if (files.Any(f => File.Exists(Path.Combine(ext, f)))) return (value, false);
        return files.Select(f => Path.Combine(ownExt, f)).FirstOrDefault(File.Exists) is { } found ? (Quote(found), true) : null;
    }

    /// <summary>The php.ini in a PHP folder of the user's; Anvil's own downloads have none.</summary>
    private string? OwnIni(string version)
    {
        var own = Path.Combine(Folder(version), "php.ini");
        return File.Exists(own) ? own : null;
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
