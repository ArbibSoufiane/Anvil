namespace Anvil.App.Tests.Support;

/// <summary>A UI thread that is waiting: what is posted to it never runs.</summary>
public sealed class BusyUiThread : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) { }

    /// <summary>Runs <paramref name="wait"/> the way the app does at exit: blocking, on a thread whose context never runs posts.</summary>
    public static Task RunBlocking(Func<ValueTask> wait)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new BusyUiThread());
            try
            {
                wait().AsTask().GetAwaiter().GetResult();
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        }) { IsBackground = true };
        thread.Start();
        return done.Task;
    }
}
