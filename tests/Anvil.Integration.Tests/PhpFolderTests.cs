using Anvil.Core.Components;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Integration.Tests.Support;

namespace Anvil.Integration.Tests;

// A real PHP build read the way Anvil reads a pvm folder: its version resource and its PE header.
public sealed class PhpFolderTests : IAsyncDisposable
{
    private readonly AppPaths _paths = Runtimes.NewRoot("ap-it-phpfolder");

    public async ValueTask DisposeAsync() => await Runtimes.DeleteRootAsync(_paths);

    [Fact]
    public async Task A_real_php_reads_as_its_release()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = await Runtimes.LatestPhpAsync("8.5", ct);
        var php = await Runtimes.InstallAsync(_paths, release, ct);

        var found = PhpFolder.Inspect(php.Path);

        Assert.Equal(new PhpFolderInfo(php.Path, release.Version, release.Toolset!, ThreadSafe: false), found);
    }
}
