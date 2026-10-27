namespace ArbiPache.Core.Apache;

public sealed record ConfigTestResult(bool Ok, string Output);

public interface IConfigTester
{
    Task<ConfigTestResult> TestAsync(CancellationToken ct);
}
