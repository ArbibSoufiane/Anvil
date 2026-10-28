using System.Text;
using Anvil.Core.Paths;

namespace Anvil.Core.Apache;

public sealed record WriteResult(IReadOnlyList<string> ChangedFiles);

/// <summary>
/// Replaces data\conf as a whole: the new tree is built in conf.next, the live tree is kept as conf.previous,
/// and <see cref="Rollback"/> puts conf.previous back unchanged.
/// </summary>
public sealed class ConfigWriter(AppPaths paths)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private string Live => paths.Conf;
    private string Next => paths.Conf + ".next";
    private string Previous => paths.Conf + ".previous";

    public WriteResult Write(RenderedConfig c)
    {
        // Folders the generated config writes into must exist before httpd -t checks them.
        Directory.CreateDirectory(Path.Combine(paths.Logs, "apache"));
        Directory.CreateDirectory(paths.Run);
        foreach (var folder in c.Folders) Directory.CreateDirectory(folder);

        var current = ReadTree(Live);
        var changed = c.Files
            .Where(f => !current.TryGetValue(f.Key, out var old) || old != f.Value)
            .Select(f => f.Key)
            .Concat(current.Keys.Where(k => !c.Files.ContainsKey(k)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (changed.Count == 0) return new WriteResult([]);

        if (Directory.Exists(Next)) Directory.Delete(Next, recursive: true);
        Directory.CreateDirectory(Path.Combine(Next, "vhosts"));
        foreach (var (relative, text) in c.Files)
        {
            var file = Path.Combine(Next, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text, Utf8);
        }

        if (Directory.Exists(Previous)) Directory.Delete(Previous, recursive: true);
        if (Directory.Exists(Live)) Directory.Move(Live, Previous);
        Directory.Move(Next, Live);
        return new WriteResult(changed);
    }

    /// <summary>Restores the tree that was live before the last <see cref="Write"/>.</summary>
    public void Rollback()
    {
        if (Directory.Exists(Live)) Directory.Delete(Live, recursive: true);
        if (Directory.Exists(Previous)) Directory.Move(Previous, Live);
        else Directory.CreateDirectory(paths.Vhosts);
    }

    private static Dictionary<string, string> ReadTree(string root) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllText, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
