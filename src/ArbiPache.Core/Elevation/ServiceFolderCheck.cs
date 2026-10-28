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

    /// <summary>SYSTEM, administrators, owner placeholders and TrustedInstaller.</summary>
    public static bool IsTrusted(string sid) => Trusted.Contains(sid);

    /// <summary>Null when only trusted accounts and the installing user can change the folder.</summary>
    public static string? Problem(string folder, IReadOnlyList<FolderAccess> access, string installingUserSid) =>
        Others(access, installingUserSid) is [_, ..] others ? Describe(folder, others) : null;

    /// <summary>The names of the accounts besides the trusted ones and the installing user that can change the folder.</summary>
    public static IReadOnlyList<string> Others(IReadOnlyList<FolderAccess> access, string installingUserSid) => access
        .Where(a => a.CanWrite && !Trusted.Contains(a.Sid) && !string.Equals(a.Sid, installingUserSid, StringComparison.OrdinalIgnoreCase))
        .Select(a => a.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Why the service is refused when <paramref name="others"/> can change the folder.</summary>
    public static string Describe(string folder, IReadOnlyList<string> others) =>
        $"{List(others)} can change files in {folder}, so {(others.Count == 1 ? "it" : "they")} could replace ArbiPache.Agent.exe and run code "
        + $"as the service. Remove {(others.Count == 1 ? "its" : "their")} write access to the folder (Properties, Security), or move "
        + "ArbiPache to a folder only you and administrators can change, then try again.";

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string List(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
}
