using System.Security.Cryptography;
using System.Text;
using Anvil.Core.IO;
using Anvil.Core.Paths;

namespace Anvil.Core.Database;

/// <summary>
/// The database root password, encrypted with DPAPI for this machine (the Agent may run as a service account) in
/// data\secrets\db-root.bin, readable only by this account and Administrators.
/// </summary>
public sealed class DbSecrets(AppPaths paths)
{
    private static readonly byte[] Entropy = "Anvil.db-root"u8.ToArray();

    private string File => Path.Combine(paths.Secrets, "db-root.bin");

    public void SaveRootPassword(string pwd)
    {
        Directory.CreateDirectory(paths.Secrets);
        var tmp = File + ".tmp";
        System.IO.File.WriteAllBytes(tmp, ProtectedData.Protect(Encoding.UTF8.GetBytes(pwd), Entropy, DataProtectionScope.LocalMachine));
        PrivateAcl.RestrictFile(tmp);
        System.IO.File.Move(tmp, File, overwrite: true);
    }

    public string? LoadRootPassword() =>
        System.IO.File.Exists(File)
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(System.IO.File.ReadAllBytes(File), Entropy, DataProtectionScope.LocalMachine))
            : null;
}
