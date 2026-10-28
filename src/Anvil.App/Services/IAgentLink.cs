namespace Anvil.App.Services;

/// <summary>The parts of <see cref="AgentConnection"/> the self-update drives.</summary>
public interface IAgentLink
{
    /// <summary>Starts no Agent until disposed, so the caller decides when the next Agent runs.</summary>
    IDisposable HoldStart();

    /// <summary>Completes once no Agent process runs any more.</summary>
    Task WhenAgentGoneAsync(CancellationToken ct);

    /// <summary>Connects again, starting the Agent if nobody answers.</summary>
    Task ConnectAsync(CancellationToken ct);
}
