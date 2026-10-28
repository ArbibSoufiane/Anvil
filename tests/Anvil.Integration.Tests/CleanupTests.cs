using Anvil.Integration.Tests.Support;

namespace Anvil.Integration.Tests;

public sealed class CleanupTests
{
    [Fact]
    public async Task A_root_whose_files_are_held_for_a_moment_is_still_deleted()
    {
        // Processes the job just ended (Apache's rotatelogs, php-cgi) keep their files open for a moment after it closes.
        var paths = Runtimes.NewRoot("ap-it-cleanup");
        var held = new FileStream(Path.Combine(paths.Logs, "held.log"), FileMode.Create, FileAccess.Write, FileShare.None);
        var release = Task.Delay(300, TestContext.Current.CancellationToken).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);

        await Runtimes.DeleteRootAsync(paths);

        await release;
        Assert.False(Directory.Exists(paths.Root));
    }
}
