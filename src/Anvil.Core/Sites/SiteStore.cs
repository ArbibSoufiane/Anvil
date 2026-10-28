using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anvil.Core.IO;
using Anvil.Core.Paths;

namespace Anvil.Core.Sites;

/// <summary>
/// One JSON file per site in data\sites. A file that cannot be read, or a site this build no longer serves, is left in
/// place and reported in Warnings. Files from older builds are upgraded in memory by <see cref="LegacySiteJson"/>.
/// </summary>
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
                    if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject json) continue;
                    if (LegacySiteJson.Upgrade(json, Path.GetFileName(file)) is { } skipped)
                    {
                        warnings.Add(skipped);
                        continue;
                    }

                    if (json.Deserialize<SiteDefinition>(JsonFiles.Options) is { } site) sites.Add(site);
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
