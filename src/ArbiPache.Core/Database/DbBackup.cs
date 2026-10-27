using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Database;

/// <summary>
/// Dumps every database of the running server to data\backups\db\&lt;yyyy-MM-dd-HHmmss&gt;.sql.gz and keeps the newest 14.
/// The folder is limited to this account and Administrators: a dump holds every row and the password hashes.
/// </summary>
public sealed partial class DbBackup(AppPaths paths, DbSecrets secrets, TimeProvider time)
{
    public const int Keep = 14;

    private string Dir => Path.Combine(paths.Backups, "db");

    public async Task<string> BackupAllAsync(DbEngine e, string version, CancellationToken ct)
    {
        var password = secrets.LoadRootPassword()
            ?? throw new InvalidOperationException("The database root password is not stored. Install the database first.");
        var port = new MyIni(paths).Read(e).GetValueOrDefault("port", "3306");
        var exe = Path.Combine(paths.DbRuntime(e, version), "bin", e == DbEngine.MariaDb ? "mariadb-dump.exe" : "mysqldump.exe");
        if (!Directory.Exists(Dir))
        {
            Directory.CreateDirectory(Dir);
            PrivateAcl.RestrictFolder(Dir);
        }

        var target = NextFile();
        var partial = target + ".partial";
        var options = DbClientOptions.Write(paths, password, int.Parse(port, CultureInfo.InvariantCulture));
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

        Prune(Dir);
        return target;
    }

    /// <summary>The next backup's path, named after the local time; a second backup in the same second gets "-2".</summary>
    internal string NextFile()
    {
        var stamp = time.GetLocalNow().ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
        var file = Path.Combine(Dir, $"{stamp}.sql.gz");
        for (var n = 2; File.Exists(file) || File.Exists(file + ".partial"); n++) file = Path.Combine(Dir, $"{stamp}-{n}.sql.gz");
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
