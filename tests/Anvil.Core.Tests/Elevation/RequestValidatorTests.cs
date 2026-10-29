using Anvil.Core.Elevation;
using Anvil.Core.Paths;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using System.Diagnostics;

namespace Anvil.Core.Tests.Elevation;

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
        var folder = Directory.CreateDirectory(dir ?? _paths.ElevationRequests).FullName;
        var file = Path.Combine(folder, $"elevate-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, json);
        return file;
    }

    private sealed record FormatDiskRequest : ElevatedRequest;

    private string SettingsFile => Path.Combine(_paths.Data, "anvil.json");

    private string? Validate(ElevatedRequest r) => RequestValidator.Validate(r, RequestFile("{}"), _paths);

    private void InstallApache(string version)
    {
        var bin = Path.Combine(_paths.ApacheRuntime(version), "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "httpd.exe"), "");
    }

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
    [InlineData("""{"verb":"service-install","agentExe":"x","controlUserSid":"S-1-5-21-1-2-3-1001"}""")]
    [InlineData("""{"verb":"service-uninstall"}""")]
    [InlineData("""{"verb":"acl-grant","grants":[]}""")]
    [InlineData("""{"verb":"folder-secure","userSid":"S-1-5-21-1-2-3-1001"}""")]
    public void Removed_verbs_are_unknown_requests(string json)
    {
        var (request, error) = RequestValidator.Read(RequestFile(json), _paths);

        Assert.Null(request);
        Assert.Equal("Unknown request.", error);
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
        InstallApache("2.4.69");

        Assert.Null(Validate(new FirewallSetRequest("2.4.69", [8080, 8443], true)));
        Assert.Null(Validate(new FirewallSetRequest(null, [], true)));
        Assert.Equal("Port 3389 is not one of Anvil's web ports (8080, 8443).", Validate(new FirewallSetRequest("2.4.69", [8080, 3389], false)));
        Assert.NotNull(Validate(new FirewallSetRequest("2.4.69", [70000], false)));
        Assert.Equal("The request lists no ports.", Validate(new FirewallSetRequest("2.4.69", null!, false)));
    }

    [Fact]
    public void Firewall_request_for_an_apache_that_is_not_installed_is_refused() =>
        Assert.Equal("Apache 2.4.70 is not installed.", Validate(new FirewallSetRequest("2.4.70", [80], false)));

    [Theory]
    [InlineData(@"..\x")]
    [InlineData(@"2.4.69\..\..\..\Windows")]
    [InlineData("2.4")]
    [InlineData("")]
    public void Firewall_request_with_a_path_in_the_version_is_refused(string version) =>
        Assert.Equal($"'{version}' is not an Apache version.", Validate(new FirewallSetRequest(version, [80], false)));

    [Fact]
    public void Validator_never_moves_a_corrupt_settings_file()
    {
        File.WriteAllText(SettingsFile, "{not json");

        Assert.Equal("Anvil's settings file cannot be read.", Validate(new FirewallSetRequest(null, [80], false)));
        Assert.Equal("{not json", File.ReadAllText(SettingsFile));
        Assert.Single(Directory.GetFiles(_paths.Data, "anvil.json*"));
    }

    [Fact]
    public void Firewall_request_round_trips_with_its_verb()
    {
        InstallApache("2.4.69");
        var file = RequestFile(ElevatedJson.Serialize(new FirewallSetRequest("2.4.69", [80, 443], true)));

        var (request, error) = RequestValidator.Read(file, _paths);

        Assert.Null(error);
        Assert.Equal(new FirewallSetRequest("2.4.69", [80, 443], true), request);
        Assert.Contains("\"verb\": \"firewall-set\"", File.ReadAllText(file));
    }

    private const string MariaDb = "\"C:\\Program Files\\MariaDB 13.0\\bin\\mysqld.exe\" \"--defaults-file={0}\" \"MariaDB\"";

    private string? ValidateWith(ElevatedRequest r, Func<string, string?> imagePathOf) =>
        RequestValidator.Validate(r, RequestFile("{}"), _paths, imagePathOf);

    private string SettingsOfService()
    {
        var ini = Path.Combine(_paths.Root, "my.ini");
        File.WriteAllText(ini, "[mysqld]\nport=3307\n");
        return ini;
    }

    [Fact]
    public void Database_service_requests_round_trip_with_their_verbs()
    {
        var access = RequestFile(ElevatedJson.Serialize(new DbServiceAccessRequest("MariaDB", Grant: true)));
        var port = RequestFile(ElevatedJson.Serialize(new DbPortRequest("MariaDB", 3310)));
        var ini = SettingsOfService();
        Func<string, string?> services = name => name == "MariaDB" ? string.Format(MariaDb, ini) : null;

        Assert.Equal(new DbServiceAccessRequest("MariaDB", true), RequestValidator.Read(access, _paths, services).Request);
        Assert.Equal(new DbPortRequest("MariaDB", 3310), RequestValidator.Read(port, _paths, services).Request);
        Assert.Contains("\"verb\": \"db-service-access\"", File.ReadAllText(access));
        Assert.Contains("\"verb\": \"db-port\"", File.ReadAllText(port));
    }

    [Fact]
    public void Only_database_services_can_be_opened_up()
    {
        Assert.Equal("There is no service named Spooler2.", ValidateWith(new DbServiceAccessRequest("Spooler2", true), _ => null));
        Assert.Equal("Spooler is not a MariaDB or MySQL service.",
            ValidateWith(new DbServiceAccessRequest("Spooler", true), _ => @"C:\Windows\System32\spoolsv.exe"));
        Assert.Equal("'..\\x' is not a service name.", ValidateWith(new DbServiceAccessRequest("..\\x", true), _ => null));
        Assert.Null(ValidateWith(new DbServiceAccessRequest("MariaDB", false), _ => string.Format(MariaDb, SettingsOfService())));
    }

    [Theory]
    [InlineData(0, "The port must be between 1 and 65535.")]
    [InlineData(70000, "The port must be between 1 and 65535.")]
    public void A_database_port_must_be_a_port(int port, string error) =>
        Assert.Equal(error, ValidateWith(new DbPortRequest("MariaDB", port), _ => string.Format(MariaDb, SettingsOfService())));

    [Fact]
    public void A_database_port_goes_only_into_the_settings_file_the_service_starts_with()
    {
        Assert.Equal("MySQL starts without a settings file, so Anvil cannot change its port.",
            ValidateWith(new DbPortRequest("MySQL", 3310), _ => @"C:\db\bin\mysqld.exe MySQL"));
        Assert.Equal(@"The settings file of MariaDB, C:\nowhere\my.ini, does not exist.",
            ValidateWith(new DbPortRequest("MariaDB", 3310), _ => string.Format(MariaDb, @"C:\nowhere\my.ini")));
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

    [Fact]
    public void Request_outside_the_elevation_folder_is_refused()
    {
        const string name = "elevate-0123456789abcdef0123456789abcdef.json";

        Assert.Equal("Requests must sit in data\\tmp\\elevation.", RequestValidator.PathError(Path.Combine(_paths.Tmp, name), _paths));
        Assert.Null(RequestValidator.PathError(Path.Combine(_paths.Tmp, "elevation", name), _paths));
        Assert.Equal("The request must be an Anvil request file.", RequestValidator.PathError(Path.Combine(_paths.Tmp, "elevation", "r.json"), _paths));
    }

    [Fact]
    public void Elevation_folder_that_links_elsewhere_is_refused()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_paths.Root, "elsewhere")).FullName;
        Directory.CreateSymbolicLink(_paths.ElevationRequests, elsewhere);

        Assert.Equal($"{_paths.ElevationRequests} is a link to another folder; the request is refused.",
            RequestValidator.PathError(Path.Combine(_paths.ElevationRequests, "elevate-0123456789abcdef0123456789abcdef.json"), _paths));
    }
}
