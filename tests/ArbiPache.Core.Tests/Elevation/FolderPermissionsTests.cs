using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using ArbiPache.Core.Elevation;

namespace ArbiPache.Core.Tests.Elevation;

/// <summary>Real folders in %TEMP%: the current user owns them, so no elevation is needed to change their permissions.</summary>
public sealed class FolderPermissionsTests : IDisposable
{
    private static readonly SecurityIdentifier Guests = new(WellKnownSidType.BuiltinGuestsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private readonly string _dir = Directory.CreateTempSubdirectory("ap-acl-").FullName;

    public void Dispose()
    {
        foreach (var link in Directory.EnumerateDirectories(_dir, "*", SearchOption.AllDirectories).Where(IsLink).ToList())
            Directory.Delete(link);
        Directory.Delete(_dir, recursive: true);
    }

    private static bool IsLink(string dir) => File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint);

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    private static FileSystemRights RightsOf(FileSystemSecurity security, SecurityIdentifier sid) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.IdentityReference.Equals(sid) && r.AccessControlType == AccessControlType.Allow)
            .Aggregate((FileSystemRights)0, (all, r) => all | r.FileSystemRights);

    private static FileSystemRights FolderRights(string dir, SecurityIdentifier sid) => RightsOf(new DirectoryInfo(dir).GetAccessControl(), sid);

    [Fact]
    public void Grant_reaches_files_already_in_the_folder()
    {
        var storage = Directory.CreateDirectory(Path.Combine(_dir, "storage", "logs")).Parent!.FullName;
        var log = Path.Combine(storage, "logs", "laravel.log");
        File.WriteAllText(log, "");

        Assert.Null(FolderPermissions.Grant(storage, Guests, AclRight.Modify));

        Assert.Equal(FileSystemRights.Modify | FileSystemRights.Synchronize, RightsOf(new FileInfo(log).GetAccessControl(), Guests));
    }

    [Theory]
    [InlineData(AclRight.Read, FileSystemRights.Read | FileSystemRights.Synchronize)]
    [InlineData(AclRight.ReadExecute, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)]
    public void Read_rights_do_not_allow_writing(AclRight right, FileSystemRights expected)
    {
        Assert.Null(FolderPermissions.Grant(_dir, Guests, right));

        Assert.Equal(expected, FolderRights(_dir, Guests));
    }

    [Fact]
    public void A_folder_that_is_a_link_is_left_alone()
    {
        var target = Directory.CreateDirectory(Path.Combine(_dir, "elsewhere")).FullName;
        var link = Path.Combine(_dir, "storage");
        Junction(link, target);

        Assert.Equal($"{link} is a link to another folder; ArbiPache did not change its permissions.",
            FolderPermissions.Grant(link, Guests, AclRight.Modify));
        Assert.Equal((FileSystemRights)0, FolderRights(target, Guests));
    }

    [Fact]
    public void A_folder_reached_through_a_linked_parent_is_left_alone()
    {
        var target = Directory.CreateDirectory(Path.Combine(_dir, "elsewhere", "storage")).FullName;
        var link = Path.Combine(_dir, "shop");
        Junction(link, Path.GetDirectoryName(target)!);
        var throughLink = Path.Combine(link, "storage");

        Assert.Equal($"{throughLink} is reached through a link to another folder; ArbiPache did not change its permissions.",
            FolderPermissions.Grant(throughLink, Guests, AclRight.Modify));
        Assert.Equal((FileSystemRights)0, FolderRights(target, Guests));
    }

    [Fact]
    public void Inherited_rights_stop_at_a_link_inside_the_folder()
    {
        var storage = Directory.CreateDirectory(Path.Combine(_dir, "storage")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside", "deep")).Parent!.FullName;
        Junction(Path.Combine(storage, "escape"), outside);

        Assert.Null(FolderPermissions.Grant(storage, Guests, AclRight.Modify));

        Assert.Equal((FileSystemRights)0, FolderRights(outside, Guests));
        Assert.Equal((FileSystemRights)0, FolderRights(Path.Combine(outside, "deep"), Guests));
    }

    [Fact]
    public void Access_lists_who_can_write_the_folder_and_its_programs()
    {
        var program = Path.Combine(_dir, "ArbiPache.Agent.dll");
        File.WriteAllText(program, "");
        var file = new FileInfo(program);
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Write, AccessControlType.Allow));
        file.SetAccessControl(security);
        var me = WindowsIdentity.GetCurrent().User!.Value;

        var access = FolderPermissions.Access(_dir);

        Assert.Contains(access, a => a.Sid == Users.Value && a.CanWrite && a.Name == @"BUILTIN\Users");
        Assert.Contains(access, a => a.Sid == me && a.CanWrite);
        Assert.DoesNotContain(access, a => a.Sid == Guests.Value);
    }

    [Fact]
    public void Allows_reports_rights_the_account_has_on_a_folder_or_above_it()
    {
        var project = Directory.CreateDirectory(Path.Combine(_dir, "crm")).FullName;
        var storage = Directory.CreateDirectory(Path.Combine(project, "storage")).FullName;
        Assert.False(FolderPermissions.Allows(storage, Guests, AclRight.Read));

        Assert.Null(FolderPermissions.Grant(project, Guests, AclRight.Read));

        Assert.True(FolderPermissions.Allows(storage, Guests, AclRight.Read));
        Assert.False(FolderPermissions.Allows(storage, Guests, AclRight.Modify));
        Assert.False(FolderPermissions.Allows(Path.Combine(project, "missing"), Guests, AclRight.Read));
    }

    [Fact]
    public void Read_only_entries_cannot_write()
    {
        Assert.Null(FolderPermissions.Grant(_dir, Guests, AclRight.ReadExecute));

        Assert.Contains(FolderPermissions.Access(_dir), a => a.Sid == Guests.Value && !a.CanWrite);
    }

    /// <summary>A folder set up like a fresh D:\: Authenticated Users may change everything below it, and this user has no entry of its own.</summary>
    private string FreshDrive()
    {
        var drive = Directory.CreateDirectory(Path.Combine(_dir, "drive")).FullName;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var (sid, rights) in new[]
                 {
                     (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl),
                     (new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl),
                     (AuthenticatedUsers, FileSystemRights.Modify),
                     (Users, FileSystemRights.ReadAndExecute),
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(drive).SetAccessControl(security);
        return drive;
    }

    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);

    [Fact]
    public void Secure_leaves_other_accounts_reading_and_this_user_writing()
    {
        var drive = FreshDrive();
        var app = Directory.CreateDirectory(Path.Combine(drive, "ArbiPache")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(app, "data")).FullName;
        var agent = Path.Combine(app, "ArbiPache.Agent.exe");
        File.WriteAllText(agent, "");
        var me = WindowsIdentity.GetCurrent().User!;
        Assert.NotNull(ServiceFolderCheck.Problem(app, FolderPermissions.Access(app), me.Value));

        Assert.Null(FolderPermissions.Secure(app, me));

        Assert.Null(ServiceFolderCheck.Problem(app, FolderPermissions.Access(app), me.Value));
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, FolderRights(app, AuthenticatedUsers));
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, RightsOf(new FileInfo(agent).GetAccessControl(), AuthenticatedUsers));
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, FolderRights(app, Users));
        File.WriteAllText(Path.Combine(data, "arbipache.json"), "{}");
        Assert.Equal(FileSystemRights.Modify | FileSystemRights.Synchronize, FolderRights(drive, AuthenticatedUsers));
    }

    [Fact]
    public void Secure_leaves_a_link_alone()
    {
        var target = Directory.CreateDirectory(Path.Combine(FreshDrive(), "ArbiPache")).FullName;
        var link = Path.Combine(_dir, "ArbiPache");
        Junction(link, target);

        Assert.Equal($"{link} is a link to another folder; ArbiPache did not change its permissions.",
            FolderPermissions.Secure(link, WindowsIdentity.GetCurrent().User!));
        Assert.Equal(FileSystemRights.Modify | FileSystemRights.Synchronize, FolderRights(target, AuthenticatedUsers));
    }
}
