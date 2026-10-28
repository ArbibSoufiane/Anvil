using System.Security.AccessControl;
using System.Security.Principal;
using Anvil.Core.IO;

namespace Anvil.Core.Tests.IO;

/// <summary>Keys and passwords are readable only by the account running Anvil and by administrators.</summary>
public sealed class PrivateAclTests : IDisposable
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly string _dir = Directory.CreateTempSubdirectory("ap-private-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Holders_are_the_account_and_administrators()
    {
        var me = new SecurityIdentifier("S-1-5-21-1004336348-1177238915-682003330-1001");

        Assert.Equal([me, Administrators], PrivateAcl.Holders(me));
    }

    [Fact]
    public void A_restricted_file_is_open_to_the_account_and_administrators_only()
    {
        var file = Path.Combine(_dir, "key.pem");
        File.WriteAllText(file, "");

        PrivateAcl.RestrictFile(file);

        var sids = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(r => r.IdentityReference).ToList();
        Assert.Equal([WindowsIdentity.GetCurrent().User!, Administrators], sids.Cast<SecurityIdentifier>().OrderBy(s => s == Administrators));
    }
}
