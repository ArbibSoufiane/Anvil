namespace ArbiPache.Core.Apache;

public sealed record ApplyOutcome(bool Ok, IReadOnlyList<string> ChangedFiles, string TestOutput);

/// <summary>Render, write, test; a configuration Apache rejects is rolled back before anything restarts.</summary>
public sealed class ConfigApplier(ConfigRenderer renderer, ConfigWriter writer, IConfigTester tester)
{
    public async Task<ApplyOutcome> ApplyAsync(RenderInput input, CancellationToken ct)
    {
        var written = writer.Write(renderer.Render(input));
        if (written.ChangedFiles.Count == 0) return new ApplyOutcome(true, [], "");

        ConfigTestResult result;
        try
        {
            result = await tester.TestAsync(ct);
        }
        catch
        {
            writer.Rollback();
            throw;
        }
        if (!result.Ok) writer.Rollback();
        return new ApplyOutcome(result.Ok, written.ChangedFiles, result.Output);
    }
}
