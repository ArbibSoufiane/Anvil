using Anvil.Core.IO;
using Anvil.Core.Paths;

namespace Anvil.Core.Database;

/// <summary>
/// A temporary [client] option file that carries the root password to mariadb-admin, mariadb-dump and their MySQL
/// twins, so it never appears on a command line. Pass it as --defaults-file and delete it afterwards.
/// </summary>
internal static class DbClientOptions
{
    public static string Write(AppPaths paths, string password, int port)
    {
        Directory.CreateDirectory(paths.Tmp);
        var file = Path.Combine(paths.Tmp, $"db-client-{Guid.NewGuid():N}.cnf");
        // Restrict the empty file first, so the password is never readable by others.
        File.WriteAllText(file, "");
        PrivateAcl.RestrictFile(file);
        File.WriteAllText(file, $"[client]\nuser=root\npassword=\"{Escape(password)}\"\nhost=127.0.0.1\nport={port}\n");
        return file;
    }

    // Option-file strings treat backslash as an escape character.
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
