using System.Text;
using Anvil.Core.Database;
using Anvil.Core.Paths;

namespace Anvil.Core.Tests.Database;

public sealed class DbSecretsTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), $"dbsecrets {Guid.NewGuid():N}"));

    public DbSecretsTests() => _paths.EnsureCreated();

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    [Fact]
    public void Secret_round_trip()
    {
        var secrets = new DbSecrets(_paths);

        secrets.SaveRootPassword("Tr0ub4dor&3 é");

        Assert.Equal("Tr0ub4dor&3 é", new DbSecrets(_paths).LoadRootPassword());
        var stored = File.ReadAllBytes(Path.Combine(_paths.Secrets, "db-root.bin"));
        Assert.Equal(-1, stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Tr0ub4dor")));
        Assert.Equal(-1, stored.AsSpan().IndexOf(Encoding.Unicode.GetBytes("Tr0ub4dor")));
    }

    [Fact]
    public void Missing_secret_is_null()
    {
        Assert.Null(new DbSecrets(_paths).LoadRootPassword());
    }

    [Fact]
    public void Saving_again_replaces_the_password()
    {
        var secrets = new DbSecrets(_paths);
        secrets.SaveRootPassword("first");

        secrets.SaveRootPassword("second");

        Assert.Equal("second", secrets.LoadRootPassword());
    }
}
