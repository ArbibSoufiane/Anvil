using Anvil.Core.Elevation;
using Anvil.Core.Paths;

namespace Anvil.Core.Tests.Elevation;

public sealed class ElevatedFoldersTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-elev-" + Guid.NewGuid().ToString("N")));

    public ElevatedFoldersTests() => _paths.EnsureCreated();

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Hosts_backups_folder_is_made_under_data()
    {
        var folder = ElevatedFolders.HostsBackups(_paths);

        Assert.Equal(Path.Combine(_paths.Backups, "hosts"), folder);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Backups_folder_that_links_elsewhere_gets_nothing_created_in_it()
    {
        // The Elevator runs as administrator; a folder created through a link the user made would land anywhere.
        var elsewhere = Directory.CreateDirectory(Path.Combine(_paths.Root, "elsewhere")).FullName;
        if (Directory.Exists(_paths.Backups)) Directory.Delete(_paths.Backups, recursive: true);
        Directory.CreateSymbolicLink(_paths.Backups, elsewhere);

        var e = Assert.Throws<IOException>(() => ElevatedFolders.HostsBackups(_paths));

        Assert.Equal($"{_paths.Backups} is a link to another folder.", e.Message);
        Assert.False(Directory.Exists(Path.Combine(elsewhere, "hosts")));
    }

    [Fact]
    public void Data_folder_that_links_elsewhere_is_refused()
    {
        var root = new AppPaths(Path.Combine(_paths.Root, "copy"));
        var elsewhere = Directory.CreateDirectory(Path.Combine(_paths.Root, "elsewhere-data")).FullName;
        Directory.CreateDirectory(root.Root);
        Directory.CreateSymbolicLink(root.Data, elsewhere);

        Assert.Throws<IOException>(() => ElevatedFolders.HostsBackups(root));
        Assert.False(Directory.Exists(Path.Combine(elsewhere, "backups")));
    }
}
