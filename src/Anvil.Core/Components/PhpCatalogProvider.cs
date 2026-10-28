using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anvil.Core.Components;

/// <summary>The newest NTS x64 build of each PHP branch, from the official windows.php.net feed.</summary>
public sealed partial class PhpCatalogProvider(HttpClient http) : ICatalogProvider
{
    // windows.php.net/downloads/releases/ now redirects here.
    private static readonly Uri Base = new("https://downloads.php.net/~windows/releases/");

    public ComponentKind Kind => ComponentKind.Php;

    public async Task<IReadOnlyList<ComponentRelease>> GetAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, "releases.json"));
        request.Headers.UserAgent.ParseAdd(Downloader.UserAgent);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);

        var releases = new List<ComponentRelease>();
        foreach (var branch in doc.RootElement.EnumerateObject())
        {
            if (!branch.Value.TryGetProperty("version", out var version)) continue;
            foreach (var build in branch.Value.EnumerateObject())
            {
                var match = NtsX64().Match(build.Name);
                if (!match.Success || !build.Value.TryGetProperty("zip", out var zip)) continue;
                releases.Add(new ComponentRelease(ComponentKind.Php,
                    // "8.5.12-1" is a rebuild of 8.5.12; the PHP version is what users see.
                    BuildSuffix().Replace(version.GetString()!, ""),
                    new Uri(Base, zip.GetProperty("path").GetString()),
                    zip.GetProperty("sha256").GetString()!.ToLowerInvariant(),
                    Toolset: match.Groups["toolset"].Value));
            }
        }
        return releases;
    }

    [GeneratedRegex(@"^nts-(?<toolset>v[cs]\d+)-x64$")]
    private static partial Regex NtsX64();

    [GeneratedRegex(@"-\d+$")]
    private static partial Regex BuildSuffix();
}
