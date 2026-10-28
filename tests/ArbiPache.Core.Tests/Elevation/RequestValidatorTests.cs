using ArbiPache.Core.Elevation;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;
using System.Diagnostics;

namespace ArbiPache.Core.Tests.Elevation;

public sealed class RequestValidatorTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-elev-" + Guid.NewGuid().ToString("N")));

    public RequestValidatorTests() => _paths.EnsureCreated();

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private string RequestFile(string json, string? dir = null)
    {
        var file = Path.Combine(dir ?? _paths.Tmp, $"elevate-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, json);
        return file;
    }

    private sealed record FormatDiskRequest : ElevatedRequest;

    [Fact]
    public void Valid_hosts_request_round_trips()
    {
        var file = RequestFile(ElevatedJson.Serialize(new HostsSetRequest(["shop.test", "xn--bcher-kva.example.com"])));

        var (request, error) = RequestValidator.Read(file, _paths);

        Assert.Null(error);
        Assert.Equal(["shop.test", "xn--bcher-kva.example.com"], Assert.IsType<HostsSetRequest>(request).Domains);
        Assert.Contains("\"verb\": \"hosts-set\"", File.ReadAllText(file));
    }

    [Fact]
    public void Unknown_verb_rejected()
    {
        var (request, error) = RequestValidator.Read(RequestFile("""{ "verb": "format-disk", "drive": "C:" }"""), _paths);

        Assert.Null(request);
        Assert.NotNull(error);
        Assert.NotNull(RequestValidator.Validate(new FormatDiskRequest(), RequestFile("{}"), _paths));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "verb": "hosts-set" }""")]
    [InlineData("""{ "verb": "hosts-set", "domains": null }""")]
    public void Invalid_payload_rejected(string json)
    {
        var (request, error) = RequestValidator.Read(RequestFile(json), _paths);

        Assert.Null(request);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("evil.com 1.2.3.4")]
    [InlineData("shop.test\r\n1.2.3.4 bank.com")]
    [InlineData("localhost")]
    [InlineData("Shop.Test")]
    [InlineData("")]
    public void Domain_with_space_rejected(string domain)
    {
        var file = RequestFile("{}");

        Assert.NotNull(RequestValidator.Validate(new HostsSetRequest(["shop.test", domain]), file, _paths));
    }

    [Fact]
    public void Firewall_ports_limited_to_configured_ports()
    {
        new SettingsStore(_paths).Save(new AppSettings { HttpPort = 8080, HttpsPort = 8443 });
        var file = RequestFile("{}");

        Assert.Null(RequestValidator.Validate(new FirewallSetRequest([8080, 8443], FirewallScope.Internet), file, _paths));
        Assert.Null(RequestValidator.Validate(new FirewallSetRequest([], FirewallScope.LocalNetwork), file, _paths));
        Assert.NotNull(RequestValidator.Validate(new FirewallSetRequest([8080, 3389], FirewallScope.Internet), file, _paths));
        Assert.NotNull(RequestValidator.Validate(new FirewallSetRequest([70000], FirewallScope.Internet), file, _paths));
        Assert.NotNull(RequestValidator.Validate(new FirewallSetRequest(null!, FirewallScope.Internet), file, _paths));
        Assert.NotNull(RequestValidator.Validate(new FirewallSetRequest([8080], (FirewallScope)7), file, _paths));
    }

    [Fact]
    public void Firewall_request_round_trips_with_its_verb()
    {
        var file = RequestFile(ElevatedJson.Serialize(new FirewallSetRequest([80, 443], FirewallScope.LocalNetwork)));

        var (request, error) = RequestValidator.Read(file, _paths);

        Assert.Null(error);
        Assert.Equal([80, 443], Assert.IsType<FirewallSetRequest>(request).Ports);
        Assert.Contains("\"verb\": \"firewall-set\"", File.ReadAllText(file));
    }

    [Fact]
    public void Request_outside_tmp_rejected()
    {
        var valid = new HostsSetRequest(["shop.test"]);

        Assert.NotNull(RequestValidator.Validate(valid, RequestFile("{}", _paths.Data), _paths));
        Assert.NotNull(RequestValidator.Validate(valid, Path.Combine(_paths.Tmp, "..", "elevate-0123456789abcdef0123456789abcdef.json"), _paths));
        Assert.NotNull(RequestValidator.Validate(valid, Path.Combine(_paths.Tmp, "other.json"), _paths));
        Assert.NotNull(RequestValidator.PathError(Path.Combine(_paths.Data, "x.json"), _paths));
        Assert.Null(RequestValidator.Validate(valid, RequestFile("{}"), _paths));
    }

    private const string UserSid = "S-1-5-21-1004336348-1177238915-682003330-1001";

    private string AgentExe()
    {
        var exe = Path.Combine(_paths.Root, "ArbiPache.Agent.exe");
        File.WriteAllText(exe, "");
        return exe;
    }

    [Fact]
    public void Service_install_next_to_the_elevator_accepted()
    {
        var file = RequestFile(ElevatedJson.Serialize(new ServiceInstallRequest(AgentExe(), UserSid)));

        var (request, error) = RequestValidator.Read(file, _paths);

        Assert.Null(error);
        Assert.Equal(UserSid, Assert.IsType<ServiceInstallRequest>(request).ControlUserSid);
        Assert.Contains("\"verb\": \"service-install\"", File.ReadAllText(file));
        Assert.Null(RequestValidator.Validate(new ServiceUninstallRequest(), RequestFile("{}"), _paths));
    }

    [Fact]
    public void Agent_exe_elsewhere_rejected()
    {
        AgentExe();
        var elsewhere = Path.Combine(_paths.Data, "ArbiPache.Agent.exe");
        File.WriteAllText(elsewhere, "");
        var file = RequestFile("{}");

        Assert.Equal("The service can only run the ArbiPache.Agent.exe next to ArbiPache.Elevator.exe.",
            RequestValidator.Validate(new ServiceInstallRequest(elsewhere, UserSid), file, _paths));
        Assert.NotNull(RequestValidator.Validate(new ServiceInstallRequest(Path.Combine(_paths.Root, "data", "..", "evil.exe"), UserSid), file, _paths));
        Assert.NotNull(RequestValidator.Validate(new ServiceInstallRequest(null!, UserSid), file, _paths));
    }

    [Fact]
    public void Agent_exe_that_is_missing_rejected()
    {
        Assert.Equal("ArbiPache.Agent.exe is missing from the ArbiPache folder.",
            RequestValidator.Validate(new ServiceInstallRequest(Path.Combine(_paths.Root, "ArbiPache.Agent.exe"), UserSid), RequestFile("{}"), _paths));
    }

    [Theory]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-11")]
    [InlineData("S-1-5-21-1-2-3")]
    [InlineData("S-1-5-21-1-2-3-4\r\nS-1-1-0")]
    [InlineData("")]
    public void Control_user_must_be_an_account_sid(string sid)
    {
        Assert.Equal("The control user must be a user account's SID.",
            RequestValidator.Validate(new ServiceInstallRequest(AgentExe(), sid), RequestFile("{}"), _paths));
    }

    [Fact]
    public void Folder_secure_request_round_trips_with_its_verb()
    {
        var file = RequestFile(ElevatedJson.Serialize(new FolderSecureRequest(UserSid)));

        var (request, error) = RequestValidator.Read(file, _paths);

        Assert.Null(error);
        Assert.Equal(UserSid, Assert.IsType<FolderSecureRequest>(request).UserSid);
        Assert.Contains("\"verb\": \"folder-secure\"", File.ReadAllText(file));
    }

    [Theory]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-11")]
    [InlineData("S-1-5-21-1-2-3-4\r\nS-1-1-0")]
    [InlineData("")]
    public void Folder_secure_keeps_writing_only_for_a_user_account(string sid)
    {
        Assert.Equal("The folder's user must be a user account's SID.",
            RequestValidator.Validate(new FolderSecureRequest(sid), RequestFile("{}"), _paths));
    }

    [Fact]
    public void Planned_acl_grants_accepted()
    {
        var file = RequestFile(ElevatedJson.Serialize(new AclGrantRequest(AclPlanner.Plan(_paths, []))));

        var (request, error) = RequestValidator.Read(file, _paths);

        Assert.Null(error);
        Assert.Equal(4, Assert.IsType<AclGrantRequest>(request).Grants.Count);
        Assert.Contains("\"verb\": \"acl-grant\"", File.ReadAllText(file));
    }

    [Fact]
    public void Acl_path_outside_known_roots_rejected()
    {
        var other = Directory.CreateDirectory(Path.Combine(_paths.Root + "-other", "Documents")).FullName;
        try
        {
            Assert.Equal($"{other} is not a folder ArbiPache's service needs.",
                RequestValidator.Validate(new AclGrantRequest([new AclGrant(other, AclRight.Read)]), RequestFile("{}"), _paths));
        }
        finally
        {
            Directory.Delete(_paths.Root + "-other", recursive: true);
        }
    }

    [Fact]
    public void Acl_right_above_the_plan_rejected()
    {
        Assert.Equal($"{_paths.Htdocs} is not a folder ArbiPache's service needs.",
            RequestValidator.Validate(new AclGrantRequest([new AclGrant(_paths.Htdocs, AclRight.Modify)]), RequestFile("{}"), _paths));
        Assert.NotNull(RequestValidator.Validate(new AclGrantRequest([new AclGrant(_paths.Data, (AclRight)9)]), RequestFile("{}"), _paths));
        Assert.NotNull(RequestValidator.Validate(new AclGrantRequest(null!), RequestFile("{}"), _paths));
        Assert.NotNull(RequestValidator.Validate(new AclGrantRequest([null!]), RequestFile("{}"), _paths));
    }

    [Fact]
    public void Acl_on_a_system_folder_rejected()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        new SiteStore(_paths).Save(new SiteDefinition { Id = "sys.test", Kind = SiteKind.Domain, Type = SiteType.Php, Name = "sys.test", Root = windows });

        Assert.Equal($"{windows} is a system folder; ArbiPache does not change its permissions.",
            RequestValidator.Validate(new AclGrantRequest([new AclGrant(windows, AclRight.Read)]), RequestFile("{}"), _paths));
    }

    [Fact]
    public void Acl_inside_program_files_rejected()
    {
        var vendor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Vendor", "app");
        new SiteStore(_paths).Save(new SiteDefinition { Id = "vendor.test", Kind = SiteKind.Domain, Type = SiteType.Php, Name = "vendor.test", Root = vendor });

        Assert.Equal($"{vendor} is a system folder; ArbiPache does not change its permissions.",
            RequestValidator.Validate(new AclGrantRequest([new AclGrant(vendor, AclRight.Read)]), RequestFile("{}"), _paths));
    }

    [Fact]
    public void Acl_for_the_chosen_htdocs_folder_accepted()
    {
        var htdocs = Directory.CreateDirectory(Path.Combine(_paths.Root + "-www")).FullName;
        try
        {
            new SettingsStore(_paths).Save(new AppSettings { HtdocsPath = htdocs });

            Assert.Null(RequestValidator.Validate(new AclGrantRequest([new AclGrant(htdocs, AclRight.Read)]), RequestFile("{}"), _paths));
        }
        finally
        {
            Directory.Delete(htdocs);
        }
    }

    [Fact]
    public void Acl_path_through_a_link_rejected()
    {
        var target = Directory.CreateDirectory(Path.Combine(_paths.Root + "-target", "shop")).FullName;
        var link = Path.Combine(_paths.Root + "-sites");
        try
        {
            var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.GetDirectoryName(target)}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            })!;
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
            var throughLink = Path.Combine(link, "shop");
            new SiteStore(_paths).Save(new SiteDefinition { Id = "shop.test", Kind = SiteKind.Domain, Type = SiteType.Php, Name = "shop.test", Root = throughLink });

            Assert.Equal($"{throughLink} goes through a link to another folder; ArbiPache does not change its permissions.",
                RequestValidator.Validate(new AclGrantRequest([new AclGrant(throughLink, AclRight.Read)]), RequestFile("{}"), _paths));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(_paths.Root + "-target", recursive: true);
        }
    }
}
