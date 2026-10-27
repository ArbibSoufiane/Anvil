namespace ArbiPache.Core.Elevation;

/// <summary>Runs one administrator action through UAC. Called from the GUI process only.</summary>
public interface IElevator
{
    Task<ElevatedResult> RunAsync(ElevatedRequest r, CancellationToken ct);
}
