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
        var file = Path.Combine(dir ?? _paths.Tmp, $"elevate-{Guid.NewGuid():N}.json");
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
}
