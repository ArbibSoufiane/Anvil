using System.Security.AccessControl;
using System.Security.Principal;
using Anvil.Core.Database;
using Anvil.Core.Paths;
using Anvil.Core.Processes;
using Windows.Win32.System.Services;

namespace Anvil.Core.Tests.Database;

/// <summary>MariaDB and MySQL servers the user installed themselves, read from their files and Windows' service list.</summary>
public sealed class LinkedServerTests : IDisposable
{
    private const string MariaDbService = "\"C:\\Program Files\\MariaDB 13.0\\bin\\mysqld.exe\" \"--defaults-file=C:\\Program Files\\MariaDB 13.0\\data\\my.ini\" \"MariaDB\"";
    private const string MySqlService = "\"C:\\Program Files\\MySQL\\MySQL Server 8.0\\bin\\mysqld.exe\" --defaults-file=\"C:\\ProgramData\\MySQL\\MySQL Server 8.0\\my.ini\" MySQL80";

    private readonly string _root = Directory.CreateTempSubdirectory("linkeddb-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_service_command_line_names_its_program_and_settings_file()
    {
        Assert.Equal(new ServiceCommand(@"C:\Program Files\MariaDB 13.0\bin\mysqld.exe", @"C:\Program Files\MariaDB 13.0\data\my.ini"),
            ServiceCommand.Parse(MariaDbService));
        Assert.Equal(new ServiceCommand(@"C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqld.exe", @"C:\ProgramData\MySQL\MySQL Server 8.0\my.ini"),
            ServiceCommand.Parse(MySqlService));
        Assert.Equal(new ServiceCommand(@"C:\db\bin\mysqld.exe", null), ServiceCommand.Parse(@"C:\db\bin\mysqld.exe MySQL"));
    }

    private string Bin(string name, params string[] exes)
    {
        var bin = Path.Combine(_root, name, "bin");
        Directory.CreateDirectory(bin);
        foreach (var exe in exes) File.WriteAllText(Path.Combine(bin, exe), "MZ");
        return bin;
    }

    private static Func<string, string?> Version(string v) => _ => v;

    [Fact]
    public void A_mariadb_folder_run_by_a_service_reads_its_port_from_the_service_settings()
    {
        var bin = Bin("MariaDB 13.0", "mariadbd.exe", "mysqld.exe", "mariadb-dump.exe");
        var ini = Path.Combine(_root, "MariaDB 13.0", "data", "my.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini, "[mysqld]\r\ndatadir=C:/x\r\nport=3307\r\n[client]\r\nport=3307\r\n");
        (string, string)[] services = [("MySQL80", MySqlService), ("MariaDB", $"\"{bin}\\mysqld.exe\" \"--defaults-file={ini}\" \"MariaDB\"")];

        var found = DbFolder.Inspect(bin, services, Version("13.0.2.0"));

        Assert.Equal(new DbFolderInfo(DbEngine.MariaDb, "13.0.2", bin, "MariaDB", ini, 3307), found);
    }

    [Fact]
    public void A_mysql_folder_without_a_service_uses_the_settings_beside_it()
    {
        var bin = Bin("mysql-8.4", "mysqld.exe", "mysqldump.exe");
        File.WriteAllText(Path.Combine(_root, "mysql-8.4", "my.ini"), "[mysqld]\nbasedir=x\n");

        var found = DbFolder.Inspect(bin, [], Version("8.4.6.0"));

        Assert.Equal(new DbFolderInfo(DbEngine.MySql, "8.4.6", bin, null, Path.Combine(_root, "mysql-8.4", "my.ini"), 3306), found);
    }

    [Fact]
    public void A_folder_without_a_server_is_refused()
    {
        var bin = Bin("empty");

        var e = Assert.Throws<ArgumentException>(() => DbFolder.Inspect(bin, [], Version("1.0.0")));
        Assert.Equal($"{bin} has no mariadbd.exe or mysqld.exe. Choose the bin folder of a MariaDB or MySQL server.", e.Message);
    }

    [Fact]
    public void The_port_is_read_from_the_server_sections()
    {
        Assert.Equal(3306, DbPortFile.Read("[client]\nport=3310\n[mysql]\n"));
        Assert.Equal(3308, DbPortFile.Read("[client]\nport=3310\n[mysqld]\nport = 3308\n"));
        Assert.Equal(3309, DbPortFile.Read("[mysqld]\nport=3308\n[mariadb]\nport=3309\n"));
    }

    [Fact]
    public void A_comment_after_the_port_is_not_part_of_it()
    {
        Assert.Equal(3307, DbPortFile.Read("[mysqld]\r\nport=3307 # moved off MySQL's port\r\n"));
        Assert.Equal(3307, DbPortFile.Read("[mysqld]\r\nport=3307#\r\n"));
    }

    [Fact]
    public void Changing_the_port_moves_the_server_and_its_client_section()
    {
        var text = "# tuned\r\n[client]\r\nport=3307\r\nplugin-dir=x\r\n[mysqld]\r\ndatadir=C:/x\r\nport=3307\r\nskip-name-resolve\r\n";

        var changed = DbPortFile.Write(text, 3310);

        Assert.Equal("# tuned\r\n[client]\r\nport = 3310\r\nplugin-dir=x\r\n[mysqld]\r\ndatadir=C:/x\r\nport = 3310\r\nskip-name-resolve\r\n", changed);
        Assert.Equal("[mysqld]\nbasedir=x\nport = 3311\n", DbPortFile.Write("[mysqld]\nbasedir=x\n", 3311));
    }

    private static byte[] ServiceDescriptor() =>
        Descriptor(new RawSecurityDescriptor("D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)"));

    private static byte[] Descriptor(RawSecurityDescriptor sd)
    {
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static readonly SecurityIdentifier User = new("S-1-5-21-1000000000-2000000000-3000000000-1001");

    [Fact]
    public void Granting_lets_the_user_start_and_stop_that_service_only_once()
    {
        var granted = ServiceAccess.Grant(ServiceDescriptor(), User)!;

        var ace = new RawSecurityDescriptor(granted, 0).DiscretionaryAcl!.OfType<CommonAce>().Single(a => a.SecurityIdentifier == User);
        Assert.Equal(AceQualifier.AccessAllowed, ace.AceQualifier);
        Assert.Equal(ServiceAccess.StartStop, ace.AccessMask);
        Assert.Null(ServiceAccess.Grant(granted, User));
    }

    [Fact]
    public void Revoking_takes_back_only_what_Anvil_granted()
    {
        var granted = ServiceAccess.Grant(ServiceDescriptor(), User)!;

        var revoked = ServiceAccess.Revoke(granted, User)!;

        Assert.Equal(new RawSecurityDescriptor(ServiceDescriptor(), 0).GetSddlForm(AccessControlSections.All),
            new RawSecurityDescriptor(revoked, 0).GetSddlForm(AccessControlSections.All));
        Assert.Null(ServiceAccess.Revoke(revoked, User));
    }

    [Fact]
    public void Linked_servers_and_their_passwords_are_kept()
    {
        var paths = new AppPaths(Path.Combine(_root, "anvil"));
        paths.EnsureCreated();
        var store = new LinkedDatabaseStore(paths);
        var server = new LinkedDatabase("mariadb", DbEngine.MariaDb, "13.0.2", @"C:\Program Files\MariaDB 13.0\bin", "MariaDB",
            @"C:\Program Files\MariaDB 13.0\data\my.ini", 3307, "root");

        store.Save(server, "root");

        Assert.Equal([server], new LinkedDatabaseStore(paths).All());
        Assert.Equal("root", store.Password("mariadb"));
        store.Remove("mariadb");
        Assert.Empty(store.All());
        Assert.Null(store.Password("mariadb"));
    }

    [Theory]
    [InlineData("MariaDB", "mariadb")]
    [InlineData("MySQL80", "mysql80")]
    [InlineData(null, "mysql-8.4.6")]
    public void A_linked_server_is_named_after_its_service_or_version(string? service, string id) =>
        Assert.Equal(id, LinkedDatabaseStore.IdFor(new DbFolderInfo(DbEngine.MySql, "8.4.6", @"C:\db\bin", service, null, 3306), []));

    [Fact]
    public void A_second_server_of_one_version_gets_its_own_name() =>
        Assert.Equal("mysql-8.4.6-2", LinkedDatabaseStore.IdFor(new DbFolderInfo(DbEngine.MySql, "8.4.6", @"C:\db2\bin", null, null, 3306), ["mysql-8.4.6"]));

    [Fact]
    public void A_service_still_stopping_has_not_stopped_yet()
    {
        Assert.False(LinkedDbHost.Reached(SERVICE_STATUS_CURRENT_STATE.SERVICE_STOP_PENDING, ProcessState.Stopped));
        Assert.True(LinkedDbHost.Reached(SERVICE_STATUS_CURRENT_STATE.SERVICE_STOPPED, ProcessState.Stopped));
        Assert.False(LinkedDbHost.Reached(SERVICE_STATUS_CURRENT_STATE.SERVICE_START_PENDING, ProcessState.Running));
        Assert.True(LinkedDbHost.Reached(SERVICE_STATUS_CURRENT_STATE.SERVICE_RUNNING, ProcessState.Running));
    }
}
