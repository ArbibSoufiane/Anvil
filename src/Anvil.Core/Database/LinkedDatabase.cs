using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Anvil.Core.IO;
using Anvil.Core.Paths;

namespace Anvil.Core.Database;

/// <summary>A MariaDB or MySQL server the user installed themselves, used where it is.</summary>
/// <param name="Service">The Windows service that runs it; null when Anvil runs it.</param>
/// <param name="DefaultsFile">The settings file it starts with, where its port is.</param>
/// <param name="User">The account Anvil logs in with for backups; its password is kept apart, encrypted.</param>
public sealed record LinkedDatabase(string Id, DbEngine Engine, string Version, string BinDir, string? Service, string? DefaultsFile, int Port, string User);

/// <summary>What a server's bin folder says about it.</summary>
public sealed record DbFolderInfo(DbEngine Engine, string Version, string BinDir, string? Service, string? DefaultsFile, int Port);

/// <summary>A service's program and the settings file its command line names.</summary>
public sealed record ServiceCommand(string Exe, string? DefaultsFile)
{
    /// <summary>Splits a service's ImagePath the way Windows splits a command line: quotes group, and are dropped.</summary>
    public static ServiceCommand Parse(string imagePath)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in imagePath)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) args.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any) args.Add(current.ToString());

        const string option = "--defaults-file=";
        var defaults = args.Skip(1).FirstOrDefault(a => a.StartsWith(option, StringComparison.OrdinalIgnoreCase))?[option.Length..];
        return new ServiceCommand(args.FirstOrDefault() ?? "", defaults);
    }

    public bool RunsServer => Path.GetFileName(Exe).ToLowerInvariant() is "mysqld.exe" or "mariadbd.exe";
}

/// <summary>Reads a MariaDB or MySQL bin folder without running anything.</summary>
public static partial class DbFolder
{
    [GeneratedRegex(@"^\d+\.\d+\.\d+")]
    private static partial Regex VersionNumber();

    /// <param name="services">Every Windows service's name and ImagePath.</param>
    /// <param name="productVersion">Reads an exe's product version.</param>
    /// <exception cref="ArgumentException">Not a server's bin folder; the message says why.</exception>
    public static DbFolderInfo Inspect(string binDir, IEnumerable<(string Name, string ImagePath)> services, Func<string, string?> productVersion)
    {
        var bin = Path.TrimEndingDirectorySeparator(Path.GetFullPath(binDir));
        var mariadbd = Path.Combine(bin, "mariadbd.exe");
        var mysqld = Path.Combine(bin, "mysqld.exe");
        var server = File.Exists(mariadbd) ? mariadbd : File.Exists(mysqld) ? mysqld
            : throw new ArgumentException($"{bin} has no mariadbd.exe or mysqld.exe. Choose the bin folder of a MariaDB or MySQL server.");
        // MariaDB before 10.5 has no mariadbd.exe, but it has always shipped mysql_install_db.exe, which MySQL 8 does not.
        var engine = server == mariadbd || File.Exists(Path.Combine(bin, "mysql_install_db.exe")) || File.Exists(Path.Combine(bin, "mariadb-install-db.exe"))
            ? DbEngine.MariaDb : DbEngine.MySql;
        var version = VersionNumber().Match(productVersion(server) ?? "");
        if (!version.Success) throw new ArgumentException($"Anvil could not read the version of {server}.");

        var service = services
            .Select(s => (s.Name, Command: ServiceCommand.Parse(s.ImagePath)))
            .FirstOrDefault(s => s.Command.RunsServer && string.Equals(Path.GetDirectoryName(s.Command.Exe), bin, StringComparison.OrdinalIgnoreCase));
        var parent = Path.GetDirectoryName(bin) ?? bin;
        var defaults = service.Name is not null
            ? service.Command.DefaultsFile
            : new[] { Path.Combine(parent, "my.ini"), Path.Combine(parent, "data", "my.ini"), Path.Combine(parent, "my.cnf") }.FirstOrDefault(File.Exists);
        var port = defaults is not null && File.Exists(defaults) ? DbPortFile.Read(File.ReadAllText(defaults)) : DbPortFile.DefaultPort;
        return new DbFolderInfo(engine, version.Value, bin, service.Name, defaults, port);
    }
}

/// <summary>The port in a server's settings file. The server reads [mysqld], [server] and its own groups; the last port wins.</summary>
public static class DbPortFile
{
    public const int DefaultPort = 3306;

    private static bool ServerSection(string name)
    {
        var n = name.ToLowerInvariant();
        return n is "mysqld" or "server" or "mariadb" or "mariadbd" || n.StartsWith("mysqld-", StringComparison.Ordinal) || n.StartsWith("mariadb-", StringComparison.Ordinal);
    }

    public static int Read(string text)
    {
        var doc = IniDocument.Parse(text);
        var port = doc.Sections().Where(ServerSection).SelectMany(s => doc.GetAll(s, "port")).LastOrDefault();
        return int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : DefaultPort;
    }

    /// <summary>Every port line of the server's groups and of [client] takes the new port, so the server's own tools follow it.</summary>
    public static string Write(string text, int port)
    {
        var doc = IniDocument.Parse(text);
        var value = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var withPort = doc.Sections().Where(s => (ServerSection(s) || s.Equals("client", StringComparison.OrdinalIgnoreCase)) && doc.Get(s, "port") is not null).ToList();
        foreach (var section in withPort) doc.Set(section, "port", value);
        if (!withPort.Any(ServerSection)) doc.Set("mysqld", "port", value);
        return doc.ToString();
    }
}

/// <summary>
/// data\linked-databases.json, and each server's password encrypted with DPAPI in data\secrets, readable only by this account
/// and Administrators.
/// </summary>
public sealed partial class LinkedDatabaseStore(AppPaths paths)
{
    private static readonly byte[] Entropy = "Anvil.db-linked"u8.ToArray();
    private static readonly Lock Gate = new();

    private string File => Path.Combine(paths.Data, "linked-databases.json");

    private string Secret(string id) => Path.Combine(paths.Secrets, $"db-linked-{id}.bin");

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex Unsafe();

    /// <summary>The service's name, or the engine and version; a file name, and unique among <paramref name="taken"/>.</summary>
    public static string IdFor(DbFolderInfo server, IReadOnlyCollection<string> taken)
    {
        var name = server.Service ?? $"{(server.Engine == DbEngine.MariaDb ? "mariadb" : "mysql")}-{server.Version}";
        var id = Unsafe().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (id.Length == 0) id = "server";
        var unique = id;
        for (var n = 2; taken.Contains(unique, StringComparer.OrdinalIgnoreCase); n++) unique = $"{id}-{n}";
        return unique;
    }

    public IReadOnlyList<LinkedDatabase> All()
    {
        lock (Gate) return System.IO.File.Exists(File) ? JsonFiles.Read<List<LinkedDatabase>>(File) ?? [] : [];
    }

    /// <summary>Adds or replaces the server; a null password keeps the one stored.</summary>
    public void Save(LinkedDatabase server, string? password = null)
    {
        lock (Gate)
        {
            if (password is not null)
            {
                Directory.CreateDirectory(paths.Secrets);
                var tmp = Secret(server.Id) + ".tmp";
                System.IO.File.WriteAllBytes(tmp, ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.LocalMachine));
                PrivateAcl.RestrictFile(tmp);
                System.IO.File.Move(tmp, Secret(server.Id), overwrite: true);
            }
            var all = System.IO.File.Exists(File) ? JsonFiles.Read<List<LinkedDatabase>>(File) ?? [] : [];
            var at = all.FindIndex(d => d.Id == server.Id);
            if (at < 0) all.Add(server);
            else all[at] = server;
            JsonFiles.Write(File, all);
        }
    }

    public string? Password(string id) =>
        System.IO.File.Exists(Secret(id))
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(System.IO.File.ReadAllBytes(Secret(id)), Entropy, DataProtectionScope.LocalMachine))
            : null;

    public void Remove(string id)
    {
        lock (Gate)
        {
            var all = All().Where(d => d.Id != id).ToList();
            JsonFiles.Write(File, all);
            System.IO.File.Delete(Secret(id));
        }
    }
}
