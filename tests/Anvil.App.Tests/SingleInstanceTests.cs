using Anvil.App.SingleInstance;

namespace Anvil.App.Tests;

public sealed class SingleInstanceTests
{
    private static string Unique(string prefix) => $"{prefix}.{Guid.NewGuid():N}";

    [Fact]
    public void Second_guard_cannot_acquire()
    {
        var name = @"Local\" + Unique("Anvil.Test");
        using var first = new InstanceGuard(name);
        using var second = new InstanceGuard(name);

        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());
    }

    [Fact]
    public void Released_guard_can_be_reacquired()
    {
        var name = @"Local\" + Unique("Anvil.Test");
        using (var first = new InstanceGuard(name)) Assert.True(first.TryAcquire());

        using var again = new InstanceGuard(name);
        Assert.True(again.TryAcquire());
    }

    [Fact]
    public async Task Activation_delivers_args_and_returns_true()
    {
        var pipe = Unique("Anvil.Test");
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ActivationServer(pipe, args => { received.TrySetResult(args); return Task.CompletedTask; });
        await server.StartAsync();

        var sent = await ActivationClient.SendAsync(pipe, ["--minimized", "D:\\Mes Sites"], TimeSpan.FromSeconds(3));

        Assert.True(sent);
        var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(["--minimized", "D:\\Mes Sites"], args);
    }

    [Fact]
    public async Task Server_accepts_more_than_one_activation()
    {
        var pipe = Unique("Anvil.Test");
        var count = 0;
        await using var server = new ActivationServer(pipe, _ => { Interlocked.Increment(ref count); return Task.CompletedTask; });
        await server.StartAsync();

        Assert.True(await ActivationClient.SendAsync(pipe, [], TimeSpan.FromSeconds(3)));
        Assert.True(await ActivationClient.SendAsync(pipe, [], TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Send_returns_false_when_no_server()
    {
        var sent = await ActivationClient.SendAsync(Unique("Anvil.Test"), [], TimeSpan.FromMilliseconds(500));

        Assert.False(sent);
    }

    [Fact]
    public void Pipe_name_contains_user_sid()
    {
        Assert.StartsWith("Anvil.App.S-1-", ActivationPipe.ForCurrentUser());
    }
}
