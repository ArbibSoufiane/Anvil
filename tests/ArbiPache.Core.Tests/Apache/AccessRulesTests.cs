using ArbiPache.Core.Apache;
using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Tests.Apache;

public sealed class AccessRulesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ap-auth-" + Guid.NewGuid().ToString("N"));

    public AccessRulesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static SiteDefinition Site(AccessLevel access) => new()
    {
        Id = "shop.example.com",
        Kind = SiteKind.Domain,
        Type = SiteType.Php,
        Name = "shop.example.com",
        Root = @"D:\Sites\shop\public",
        Access = access,
    };

    private static void AssertRules(string expected, string actual) => Assert.Equal(expected.ReplaceLineEndings("\n"), actual);

    [Fact]
    public void Everyone_is_all_granted() => AssertRules("Require all granted", AccessRules.Render(Site(AccessLevel.Everyone), null));

    [Fact]
    public void Network_is_private_ranges() =>
        AssertRules("Require ip 127.0.0.0/8 ::1 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 fc00::/7 fe80::/10",
            AccessRules.Render(Site(AccessLevel.Network), null));

    [Fact]
    public void This_pc_is_local() => AssertRules("Require local", AccessRules.Render(Site(AccessLevel.ThisPc), null));

    [Fact]
    public void Specific_ips_keep_local_and_list_addresses() =>
        AssertRules("""
            Require local
            Require ip 203.0.113.7 198.51.100.0/24
            """,
            AccessRules.Render(Site(AccessLevel.SpecificIps) with { AllowedIps = ["203.0.113.7", "198.51.100.0/24"] }, null));

    [Fact]
    public void Password_wraps_in_RequireAll() =>
        AssertRules("""
            AuthType Basic
            AuthName "shop.example.com"
            AuthBasicProvider file
            AuthUserFile "D:/ArbiPache/data/auth/shop.example.com.htpasswd"
            <RequireAll>
                <RequireAny>
                    Require local
                    Require ip 203.0.113.7
                </RequireAny>
                Require valid-user
            </RequireAll>
            """,
            AccessRules.Render(Site(AccessLevel.SpecificIps) with { AllowedIps = ["203.0.113.7"], PasswordProtected = true },
                @"D:\ArbiPache\data\auth\shop.example.com.htpasswd"));

    [Fact]
    public void Password_protected_site_without_htpasswd_file_is_refused() =>
        Assert.Throws<ArgumentException>(() => AccessRules.Render(Site(AccessLevel.Everyone) with { PasswordProtected = true }, null));

    [Fact]
    public void Only_from_proxies_adds_conn_remote_addr_expr() =>
        AssertRules("""
            <RequireAll>
                <RequireAny>
                    Require all granted
                </RequireAny>
                Require expr "%{CONN_REMOTE_ADDR} -ipmatch '10.0.0.5' || %{CONN_REMOTE_ADDR} -ipmatch '10.0.1.0/24'"
            </RequireAll>
            """,
            AccessRules.Render(Site(AccessLevel.Everyone) with
            {
                Cert = CertSource.LoadBalancer, TrustedProxies = ["10.0.0.5", "10.0.1.0/24"], AcceptOnlyFromProxies = true,
            }, null));

    [Fact]
    public void Password_and_proxies_share_one_RequireAll() =>
        AssertRules("""
            AuthType Basic
            AuthName "shop.example.com"
            AuthBasicProvider file
            AuthUserFile "D:/auth/shop.htpasswd"
            <RequireAll>
                <RequireAny>
                    Require all granted
                </RequireAny>
                Require expr "%{CONN_REMOTE_ADDR} -ipmatch '10.0.0.5'"
                Require valid-user
            </RequireAll>
            """,
            AccessRules.Render(Site(AccessLevel.Everyone) with
            {
                Cert = CertSource.LoadBalancer, TrustedProxies = ["10.0.0.5"], AcceptOnlyFromProxies = true, PasswordProtected = true,
            }, @"D:\auth\shop.htpasswd"));

    [Fact]
    public void Htpasswd_bcrypt_verifies()
    {
        var file = Path.Combine(_dir, "shop.htpasswd");

        HtpasswdFile.Write(file, "client", "s3cret pass");

        var line = File.ReadAllText(file).TrimEnd();
        Assert.StartsWith("client:$2y$10$", line);
        Assert.Single(line.Split('\n'));
        Assert.True(HtpasswdFile.Verify(file, "client", "s3cret pass"));
        Assert.False(HtpasswdFile.Verify(file, "client", "wrong"));
        Assert.False(HtpasswdFile.Verify(file, "other", "s3cret pass"));
        Assert.False(HtpasswdFile.Verify(Path.Combine(_dir, "missing.htpasswd"), "client", "s3cret pass"));
    }

    [Fact]
    public void Htpasswd_write_replaces_previous_user()
    {
        var file = Path.Combine(_dir, "shop.htpasswd");
        HtpasswdFile.Write(file, "client", "one");

        HtpasswdFile.Write(file, "admin", "two");

        Assert.False(HtpasswdFile.Verify(file, "client", "one"));
        Assert.True(HtpasswdFile.Verify(file, "admin", "two"));
    }

    [Theory]
    [InlineData("", "pw")]
    [InlineData("a:b", "pw")]
    [InlineData("a\nb", "pw")]
    [InlineData("client", "")]
    public void Htpasswd_rejects_bad_user_or_empty_password(string user, string password) =>
        Assert.Throws<ArgumentException>(() => HtpasswdFile.Write(Path.Combine(_dir, "x.htpasswd"), user, password));
}
