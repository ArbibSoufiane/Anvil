using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArbiPache.Core.Components;

/// <summary>The newest Windows x64 zip of each stable MariaDB series, from the downloads.mariadb.org REST API.</summary>
public sealed partial class MariaDbCatalogProvider(HttpClient http, TimeProvider? time = null) : ICatalogProvider
{
    private static readonly Uri Base = new("https://downloads.mariadb.org/rest-api/mariadb/");

    // The API's download links redirect to a nearby mirror, which can crawl; MariaDB's own archive serves every release.
    private static readonly Uri Archive = new("https://archive.mariadb.org/");

    public ComponentKind Kind => ComponentKind.MariaDb;

    public async Task<IReadOnlyList<ComponentRelease>> GetAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime((time ?? TimeProvider.System).GetUtcNow().UtcDateTime);
        var series = new List<(string Id, bool Lts, DateOnly? EndOfLife)>();
        using (var majors = await GetJsonAsync(Base, ct))
        {
            foreach (var major in majors.RootElement.GetProperty("major_releases").EnumerateArray())
            {
                // Preview and RC series are for testing MariaDB itself, not for serving sites.
                if (Text(major, "release_status") != "Stable" || Text(major, "release_id") is not { } id) continue;
                var eol = DateOnly.TryParse(Text(major, "release_eol_date"), CultureInfo.InvariantCulture, out var end) ? end : (DateOnly?)null;
                series.Add((id, Text(major, "release_support_type") == "Long Term Support", eol));
            }
        }

        var releases = new List<ComponentRelease>();
        foreach (var (id, lts, eol) in series)
        {
            using var latest = await GetJsonAsync(new Uri(Base, $"{id}/latest/"), ct);
            foreach (var release in latest.RootElement.GetProperty("releases").EnumerateObject())
            {
                foreach (var file in release.Value.GetProperty("files").EnumerateArray())
                {
                    if (Text(file, "file_name") is not { } name || !WinX64Zip().IsMatch(name)) continue;
                    if (!file.TryGetProperty("checksum", out var checksum) || Text(checksum, "sha256sum") is not { } sha256) continue;
                    releases.Add(new ComponentRelease(ComponentKind.MariaDb, release.Name,
                        new Uri(Archive, $"mariadb-{release.Name}/winx64-packages/{name}"), sha256.ToLowerInvariant(),
                        StripRoot: Path.GetFileNameWithoutExtension(name), Lts: lts,
                        Notes: eol is { } end && today > end ? "End of life" : null));
                }
            }
        }
        return releases;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(Downloader.UserAgent);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct);
    }

    private static string? Text(JsonElement e, string property) =>
        e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // The server zip only: the .msi installer and the -debugsymbols zip are skipped.
    [GeneratedRegex(@"^mariadb-[\d.]+-winx64\.zip$")]
    private static partial Regex WinX64Zip();
}
