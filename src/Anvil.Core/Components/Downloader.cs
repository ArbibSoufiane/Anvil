using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Anvil.Core.Components;

/// <summary>Downloads over https and keeps a file only when its SHA-256 matches the catalog.</summary>
public sealed class Downloader(HttpClient http)
{
    private const string ApacheLoungeHost = "apachelounge.com";

    public static string UserAgent { get; } = $"Anvil/{BuildInfo.Version} (+https://github.com/ArbibSoufiane/Anvil)";

    public async Task<string> FetchAsync(Uri url, string expectedSha256, string destFile, IProgress<long>? progress, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException($"Downloads must use https: {url}", nameof(url));

        if (File.Exists(destFile) && SameHash(await HashFileAsync(destFile, ct), expectedSha256))
            return destFile;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destFile))!);
        var part = destFile + ".part";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        // Apache Lounge rejects requests without a referer from its own download page (hotlink protection).
        if (url.Host.Equals(ApacheLoungeHost, StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith("." + ApacheLoungeHost, StringComparison.OrdinalIgnoreCase))
            request.Headers.Referrer = new Uri("https://www.apachelounge.com/download/");

        string actual;
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
                progress?.Report(total);
            }
            actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        if (!SameHash(actual, expectedSha256))
        {
            File.Delete(part);
            throw new ChecksumMismatchException(expectedSha256.ToLowerInvariant(), actual);
        }
        File.Move(part, destFile, overwrite: true);
        return destFile;
    }

    private static bool SameHash(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
