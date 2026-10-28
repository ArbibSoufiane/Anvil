using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anvil.Core.Components;

namespace Anvil.Core.Update;

/// <summary>An Anvil release on GitHub: the Windows zip and the SHA256SUMS file that vouches for it.</summary>
public sealed record Release(string Version, Uri ZipUrl, Uri SumsUrl, string Notes);

/// <summary>Asks GitHub for the latest Anvil release, only when the user asks: there is no background check.</summary>
public sealed partial class ReleaseFeed(HttpClient http, string repository = "ArbibSoufiane/Anvil")
{
    public const string SumsName = "SHA256SUMS";

    public static string ZipName(string version) => $"Anvil-{version}-win64.zip";

    /// <summary>
    /// The latest published release when it is newer than <paramref name="current"/>. Drafts, pre-releases and releases
    /// without both the zip and SHA256SUMS are not offered.
    /// </summary>
    /// <exception cref="HttpRequestException">GitHub could not be reached.</exception>
    /// <exception cref="InvalidOperationException">GitHub refused the check.</exception>
    public async Task<Release?> CheckAsync(string current, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd(Downloader.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("GitHub refused the check for now (too many requests from this address). Try again in an hour.");
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        if (Flag(root, "draft") || Flag(root, "prerelease")) return null;
        var version = (Text(root, "tag_name") ?? "").TrimStart('v', 'V');
        if (!Numbered().IsMatch(version) || SemVer.Compare(version, current) <= 0) return null;

        Uri? zip = null, sums = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                if (!Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) continue;
                var name = Text(asset, "name");
                if (name == ZipName(version)) zip = url;
                else if (name == SumsName) sums = url;
            }
        return zip is null || sums is null ? null : new Release(version, zip, sums, Text(root, "body") ?? "");
    }

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex Numbered();
}
