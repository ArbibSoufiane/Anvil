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
}
