using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Anvil.Core.IO;
using Anvil.Core.Paths;

namespace Anvil.Core.Database;

/// <summary>
/// Dumps every database of a running linked server to data\backups\db\&lt;id&gt;\&lt;yyyy-MM-dd-HHmmss&gt;.sql.gz and keeps the newest 14.
/// The folder is limited to this account and Administrators: a dump holds every row and the password hashes.
/// </summary>
public sealed partial class DbBackup(AppPaths paths, TimeProvider time)
{
    public const int Keep = 14;

    private string Dir => Path.Combine(paths.Backups, "db");

    /// <summary>Dumped with the tool in the server's own bin folder.</summary>
    public Task<string> BackupLinkedAsync(LinkedDatabase server, string password, CancellationToken ct)
    {
        var dump = new[] { "mariadb-dump.exe", "mysqldump.exe" }.Select(n => Path.Combine(server.BinDir, n)).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException($"{server.BinDir} has no mariadb-dump.exe or mysqldump.exe.");
        return DumpAsync(Path.Combine(Dir, server.Id), dump, server.User, password, server.Port, ct);
    }

    private async Task<string> DumpAsync(string dir, string exe, string user, string password, int port, CancellationToken ct)
    {
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            PrivateAcl.RestrictFolder(dir);
        }

        var target = NextFile(dir);
        var partial = target + ".partial";
        var options = DbClientOptions.Write(paths, password, port, user);
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            foreach (var arg in new[] { $"--defaults-file={options}", "--all-databases", "--single-transaction", "--routines", "--events", "--triggers" })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            try
            {
                var stderr = process.StandardError.ReadToEndAsync(ct);
                await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write))
                await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                    await process.StandardOutput.BaseStream.CopyToAsync(gzip, ct);
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"{Path.GetFileName(exe)} failed (exit {process.ExitCode}). {(await stderr).Trim()}");
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            File.Move(partial, target);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
        finally
        {
            File.Delete(options);
        }

        Prune(dir);
        return target;
    }

    /// <summary>The next backup's path, named after the local time; a second backup in the same second gets "-2".</summary>
    internal string NextFile(string? dir = null)
    {
        dir ??= Dir;
        var stamp = time.GetLocalNow().ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
        var file = Path.Combine(dir, $"{stamp}.sql.gz");
        for (var n = 2; File.Exists(file) || File.Exists(file + ".partial"); n++) file = Path.Combine(dir, $"{stamp}-{n}.sql.gz");
        return file;
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> backups, judged by the time in their names. Other files stay.</summary>
    public static IReadOnlyList<string> Prune(string dir, int keep = Keep)
    {
        if (!Directory.Exists(dir)) return [];
        var removed = Directory.EnumerateFiles(dir, "*.sql.gz")
            .Select(f => (File: f, Match: BackupName().Match(Path.GetFileName(f))))
            .Where(x => x.Match.Success)
            .OrderByDescending(x => x.Match.Groups["stamp"].Value, StringComparer.Ordinal)
            .ThenByDescending(x => x.Match.Groups["n"].Success ? int.Parse(x.Match.Groups["n"].Value, CultureInfo.InvariantCulture) : 1)
            .Skip(keep)
            .Select(x => x.File)
            .ToList();
        foreach (var file in removed) File.Delete(file);
        return removed;
    }

    [GeneratedRegex(@"^(?<stamp>\d{4}-\d{2}-\d{2}-\d{6})(-(?<n>\d+))?\.sql\.gz$")]
    private static partial Regex BackupName();
}
