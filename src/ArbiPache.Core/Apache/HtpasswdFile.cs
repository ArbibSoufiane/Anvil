using ArbiPache.Core.IO;

namespace ArbiPache.Core.Apache;

/// <summary>A site's password file: exactly one user, bcrypt ($2y$, cost 10), the format Apache's mod_authn_file reads.</summary>
public static class HtpasswdFile
{
    private const int WorkFactor = 10;

    public static void Write(string path, string user, string password)
    {
        if (user.Length == 0 || user.IndexOfAny([':', '\r', '\n']) >= 0)
            throw new ArgumentException("The user name cannot be empty or contain ':' or line breaks.", nameof(user));
        if (password.Length == 0) throw new ArgumentException("The password cannot be empty.", nameof(password));

        var hash = BCrypt.Net.BCrypt.HashPassword(password, BCrypt.Net.BCrypt.GenerateSalt(WorkFactor, 'y'));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllText(path, $"{user}:{hash}\n");
    }

    public static bool Verify(string path, string user, string password)
    {
        if (!File.Exists(path)) return false;
        foreach (var line in File.ReadAllLines(path))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon] == user) return BCrypt.Net.BCrypt.Verify(password, line[(colon + 1)..]);
        }

        return false;
    }
}
