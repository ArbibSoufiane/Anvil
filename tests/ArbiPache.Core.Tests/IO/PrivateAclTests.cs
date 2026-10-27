using System.Security.AccessControl;
using System.Security.Principal;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.IO;

namespace ArbiPache.Core.Tests.IO;

/// <summary>
/// Keys and passwords are readable by whoever runs the Agent: the user's child Agent and the service read each other's
/// files, so both accounts are always on them.
/// </summary>
public sealed class PrivateAclTests : IDisposable
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly string _dir = Directory.CreateTempSubdirectory("ap-private-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Holders_are_the_account_administrators_the_service_and_its_control_users()
    {
        var me = new SecurityIdentifier("S-1-5-21-1004336348-1177238915-682003330-1001");

        var holders = PrivateAcl.Holders(me, ["S-1-5-21-1004336348-1177238915-682003330-1002", me.Value]);

        Assert.Equal([me, Administrators, AclPlanner.ServiceSid, new SecurityIdentifier("S-1-5-21-1004336348-1177238915-682003330-1002")], holders);
    }

    [Fact]
    public void The_service_runs_as_its_own_holder_once()
    {
        Assert.Equal([AclPlanner.ServiceSid, Administrators], PrivateAcl.Holders(AclPlanner.ServiceSid, []));
    }

    [Fact]
    public void A_restricted_file_is_open_to_the_service()
    {
        var file = Path.Combine(_dir, "key.pem");
        File.WriteAllText(file, "");

        PrivateAcl.RestrictFile(file);

        var sids = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(r => r.IdentityReference).ToList();
        Assert.Contains(AclPlanner.ServiceSid, sids);
        Assert.Contains(WindowsIdentity.GetCurrent().User!, sids);
        Assert.Equal(3, sids.Count);
    }
}
