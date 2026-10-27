using System.Text.Json;
using System.Text.RegularExpressions;
using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Sites;

/// <summary>One JSON file per site in data\sites. A file that cannot be read is left in place and reported in Warnings.</summary>
public sealed partial class SiteStore(AppPaths paths)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex SafeId();

    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public IReadOnlyList<SiteDefinition> All()
    {
        var sites = new List<SiteDefinition>();
        var warnings = new List<string>();
        if (Directory.Exists(paths.Sites))
        {
            foreach (var file in Directory.GetFiles(paths.Sites, "*.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    if (JsonFiles.Read<SiteDefinition>(file) is { } site) sites.Add(site);
                }
                catch (JsonException ex)
                {
                    warnings.Add($"{Path.GetFileName(file)} could not be read ({ex.Message}). That site is not served until the file is fixed.");
                }
            }
        }

        Warnings = warnings;
        return sites;
    }

    public void Save(SiteDefinition s) => JsonFiles.Write(FileFor(s.Id), s);

    public void Delete(string id)
    {
        var file = FileFor(id);
        if (File.Exists(file)) File.Delete(file);
    }

    /// <summary>Throws unless <paramref name="id"/> is safe as a single file or folder name.</summary>
    internal static void EnsureSafeId(string id)
    {
        if (!SafeId().IsMatch(id)) throw new ArgumentException($"'{id}' is not a valid site id.", nameof(id));
    }

    private string FileFor(string id)
    {
        EnsureSafeId(id);
        return Path.Combine(paths.Sites, id + ".json");
    }
}
