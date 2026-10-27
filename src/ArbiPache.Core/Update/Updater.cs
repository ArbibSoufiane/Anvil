using System.Diagnostics;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Update;

/// <summary>The command line the old window starts the staged ArbiPache.exe with.</summary>
public sealed record ApplyUpdateArgs(string Staged, string Target, int Pid)
{
    public const string Flag = "--apply-update";

    public IReadOnlyList<string> ToArgs() => [Flag, Staged, "--target", Target, "--pid", Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    /// <summary>Null unless the arguments are exactly <c>--apply-update &lt;staged&gt; --target &lt;root&gt; --pid &lt;n&gt;</c>.</summary>
    public static ApplyUpdateArgs? Parse(IReadOnlyList<string> args) =>
        args is [Flag, var staged, "--target", var target, "--pid", var pid] && int.TryParse(pid, out var n) && n > 0
            ? new ApplyUpdateArgs(staged, target, n)
            : null;
}

/// <summary>
/// Updates ArbiPache itself: downloads a release into data-free <c>.update\</c>, checks it, and swaps only the program
/// files. <c>runtime\</c>, <c>data\</c> and <c>htdocs\</c> are never touched, nor any folder the release does not
/// bring; the replaced files wait in <c>.update\previous-&lt;version&gt;\</c>.
/// </summary>
/// <param name="requiredPublisher">Once releases are signed: the certificate name every program file must carry.</param>
public sealed class Updater(AppPaths paths, HttpClient http, string? requiredPublisher = null, Func<string, string, bool>? isSignedBy = null)
{
    public static readonly TimeSpan ExitWait = TimeSpan.FromMinutes(1);

    private static readonly string[] Programs = ["ArbiPache.exe", "ArbiPache.Agent.exe", "ArbiPache.Elevator.exe"];
    private static readonly HashSet<string> NeverSwapped = new(["runtime", "data", "htdocs", ".update"], StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string, bool> _isSignedBy = isSignedBy ?? AuthenticodeCheck.IsSignedBy;

    public string UpdateDir => Path.Combine(paths.Root, ".update");

    /// <summary>Downloads, verifies and unpacks the release; returns the folder that holds its ArbiPache.exe.</summary>
    /// <exception cref="ChecksumMismatchException">The zip is not the one SHA256SUMS names.</exception>
    /// <exception cref="InvalidOperationException">The release cannot be checked, is not ArbiPache, or is not signed.</exception>
    public async Task<string> StageAsync(Release r, IProgress<long>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdateDir).Attributes |= FileAttributes.Hidden;
        var zipName = Path.GetFileName(r.ZipUrl.AbsolutePath);
        var expected = Sum(await ReadSumsAsync(r.SumsUrl, ct), zipName)
            ?? throw new InvalidOperationException($"The release's {ReleaseFeed.SumsName} does not list {zipName}, so the download cannot be checked.");
        var zip = await new Downloader(http).FetchAsync(r.ZipUrl, expected, Path.Combine(UpdateDir, "download", zipName), progress, ct);

        var staged = Path.Combine(UpdateDir, $"staged-{r.Version}");
        try
        {
            if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
            ZipExtractor.Extract(zip, staged);
            var root = HoldsPrograms(staged) ? staged
                : Directory.GetDirectories(staged) is [var only] && HoldsPrograms(only) ? only
                : throw new InvalidOperationException(
                    "The download is not an ArbiPache release: ArbiPache.exe, ArbiPache.Agent.exe and ArbiPache.Elevator.exe must be in it.");
            if (requiredPublisher is { } publisher)
            {
                var unsigned = Directory.EnumerateFiles(root).Where(IsOurs).Order(StringComparer.OrdinalIgnoreCase)
                    .Where(f => !_isSignedBy(f, publisher)).Select(Path.GetFileName).ToList();
                if (unsigned.Count > 0)
                    throw new InvalidOperationException(
                        $"{string.Join(", ", unsigned)} in the download {(unsigned.Count == 1 ? "is" : "are")} not signed by {publisher}, so the update was refused.");
            }
            return root;
        }
        catch
        {
            TryDelete(staged);
            throw;
        }
        finally
        {
            TryDelete(zip);
        }
    }

    /// <summary>
    /// Runs in the staged ArbiPache.exe once the old window closed: moves the old program files aside, copies the new ones
    /// in, and puts the old ones back when anything fails.
    /// </summary>
    /// <exception cref="InvalidOperationException">The old window is still open, or a file could not be replaced.</exception>
    public static void Swap(string staged, string target, int pid, TimeSpan? wait = null)
    {
        WaitForExit(pid, wait ?? ExitWait);
        var update = Path.Combine(target, ".update");
        var previous = Path.Combine(update, $"previous-{InstalledVersion(target)}");
        if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
        Directory.CreateDirectory(previous);

        var incoming = Directory.EnumerateFileSystemEntries(staged).Where(p => !NeverSwapped.Contains(Path.GetFileName(p)))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var incomingFolders = incoming.Where(Directory.Exists).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Every old file goes, so none lingers from an older version; a folder only when the release brings its own.
        var outgoing = Directory.EnumerateFiles(target)
            .Concat(Directory.EnumerateDirectories(target).Where(d => incomingFolders.Contains(Path.GetFileName(d))))
            .Where(p => !NeverSwapped.Contains(Path.GetFileName(p)))
            .ToList();

        var moved = new List<string>();
        var created = new List<string>();
        try
        {
            foreach (var path in outgoing)
            {
                Move(path, Path.Combine(previous, Path.GetFileName(path)));
                moved.Add(path);
            }
            foreach (var path in incoming)
            {
                var dest = Path.Combine(target, Path.GetFileName(path));
                if (File.Exists(dest) || Directory.Exists(dest)) throw new IOException($"{dest} is in the way.");
                created.Add(dest);
                Copy(path, dest);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var path in created) TryDelete(path);
            foreach (var path in moved) Move(Path.Combine(previous, Path.GetFileName(path)), path);
            throw new InvalidOperationException($"ArbiPache could not replace its files ({e.Message}). The previous version was put back.", e);
        }

        foreach (var older in Directory.GetDirectories(update, "previous-*").Where(d => !string.Equals(d, previous, StringComparison.OrdinalIgnoreCase)))
            TryDelete(older);
    }

    /// <summary>On the next start: drops finished staging folders and downloads, and keeps only the newest previous version.</summary>
    public void CleanUp()
    {
        if (!Directory.Exists(UpdateDir)) return;
        foreach (var dir in Directory.GetDirectories(UpdateDir, "staged-*").Append(Path.Combine(UpdateDir, "download"))) TryDelete(dir);
        foreach (var older in Directory.GetDirectories(UpdateDir, "previous-*")
                     .OrderByDescending(d => Version.TryParse(Path.GetFileName(d)["previous-".Length..], out var v) ? v : new Version(0, 0))
                     .Skip(1))
            TryDelete(older);
    }

    private async Task<string> ReadSumsAsync(Uri url, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException($"{ReleaseFeed.SumsName} must come over https: {url}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(Downloader.UserAgent);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    // sha256sum lines: "<hash>  <name>", or "<hash> *<name>" in binary mode.
    private static string? Sum(string sums, string name) =>
        sums.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(p => p.Length == 2 && p[0].Length == 64 && p[1].TrimStart('*') == name)?[0];

    private static bool HoldsPrograms(string dir) => Programs.All(p => File.Exists(Path.Combine(dir, p)));

    private static bool IsOurs(string file)
    {
        var name = Path.GetFileName(file);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("ArbiPache", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
    }

    private static void WaitForExit(int pid, TimeSpan wait)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return; // Gone already.
        }
        using (process)
            if (!process.WaitForExit(wait))
                throw new InvalidOperationException($"ArbiPache (process {pid}) is still running, so its files cannot be replaced.");
    }

    private static string InstalledVersion(string target)
    {
        var exe = Path.Combine(target, "ArbiPache.exe");
        var version = File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion : null;
        if (string.IsNullOrWhiteSpace(version)) return "unknown";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private static void Copy(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            File.Copy(from, to);
            return;
        }
        Directory.CreateDirectory(to);
        foreach (var entry in Directory.EnumerateFileSystemEntries(from)) Copy(entry, Path.Combine(to, Path.GetFileName(entry)));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still in use (the staged ArbiPache.exe while it swaps): the next start removes it.
        }
    }
}
