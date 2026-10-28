using Anvil.Core.Elevation;

namespace Anvil.Core.Tests.Elevation;

/// <summary>The service runs Anvil.Agent.exe from the app folder, so nobody but administrators and the installer may change it.</summary>
public sealed class ServiceFolderCheckTests
{
    private const string User = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string OtherUser = "S-1-5-21-1004336348-1177238915-682003330-1002";

    [Fact]
    public void Writable_only_by_admins_system_and_the_installer_accepted()
    {
        Assert.Null(ServiceFolderCheck.Problem(@"D:\Anvil",
        [
            new FolderAccess("S-1-5-32-544", @"BUILTIN\Administrators", CanWrite: true),
            new FolderAccess("S-1-5-18", @"NT AUTHORITY\SYSTEM", CanWrite: true),
            new FolderAccess(User, @"PC\soufi", CanWrite: true),
            new FolderAccess("S-1-5-32-545", @"BUILTIN\Users", CanWrite: false),
        ], User));
    }

    [Fact]
    public void Writable_by_other_users_refused()
    {
        var problem = ServiceFolderCheck.Problem(@"D:\Anvil",
        [
            new FolderAccess("S-1-5-32-544", @"BUILTIN\Administrators", CanWrite: true),
            new FolderAccess("S-1-5-11", @"NT AUTHORITY\Authenticated Users", CanWrite: true),
            new FolderAccess(OtherUser, @"PC\guest", CanWrite: true),
            new FolderAccess(OtherUser, @"PC\guest", CanWrite: true),
        ], User);

        Assert.Equal(@"NT AUTHORITY\Authenticated Users and PC\guest can change files in D:\Anvil, so they could replace "
            + "Anvil.Agent.exe and run code as the service. Remove their write access to the folder (Properties, Security), "
            + "or move Anvil to a folder only you and administrators can change, then try again.", problem);
    }
}
