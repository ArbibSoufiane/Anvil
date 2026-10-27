namespace ArbiPache.Core.Elevation;

/// <summary>One allow entry on the app folder or one of its programs.</summary>
/// <param name="CanWrite">The entry lets the account create, change, delete or re-permission files.</param>
public sealed record FolderAccess(string Sid, string Name, bool CanWrite);

/// <summary>
/// The service runs ArbiPache.Agent.exe and its DLLs from the app folder. Anyone else who can change that folder could
/// replace them and run code as the service, so installing is refused until only trusted accounts can.
/// </summary>
public static class ServiceFolderCheck
{
    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18",      // SYSTEM
        "S-1-5-32-544",  // Administrators
        "S-1-3-0",       // CREATOR OWNER: only the account that created the file
        "S-1-3-4",       // OWNER RIGHTS
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // TrustedInstaller
    };

    /// <summary>Null when only trusted accounts and the installing user can change the folder.</summary>
    public static string? Problem(string folder, IReadOnlyList<FolderAccess> access, string installingUserSid)
    {
        var others = access
            .Where(a => a.CanWrite && !Trusted.Contains(a.Sid) && !string.Equals(a.Sid, installingUserSid, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (others.Count == 0) return null;
        var names = others.Count == 1 ? others[0] : $"{string.Join(", ", others[..^1])} and {others[^1]}";
        return $"{names} can change files in {folder}, so {(others.Count == 1 ? "it" : "they")} could replace ArbiPache.Agent.exe and run code "
            + $"as the service. Remove {(others.Count == 1 ? "its" : "their")} write access to the folder (Properties, Security), or move "
            + "ArbiPache to a folder only you and administrators can change, then try again.";
    }
}
