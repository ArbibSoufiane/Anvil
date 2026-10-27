using System.Diagnostics;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Apache;

/// <summary>Runs <c>httpd.exe -t</c> against data\apache\httpd.conf with the active Apache version.</summary>
public sealed class ApacheConfigTester(AppPaths paths, Func<string?> activeApacheVersion) : IConfigTester
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public async Task<ConfigTestResult> TestAsync(CancellationToken ct)
    {
        var version = activeApacheVersion();
        if (version is null) return new ConfigTestResult(false, "No Apache version is active.");
        var root = paths.ApacheRuntime(version);
        var httpd = Path.Combine(root, "bin", "httpd.exe");
        if (!File.Exists(httpd)) return new ConfigTestResult(false, $"Apache {version} is not installed: {httpd} is missing.");

        var psi = new ProcessStartInfo(httpd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(AppPaths.ForApache(root));
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(AppPaths.ForApache(paths.HttpdConf));

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new ConfigTestResult(false, $"httpd -t did not finish within {Limit.TotalSeconds:0} seconds.");
        }
        var output = ((await stdout) + (await stderr)).Trim();
        return new ConfigTestResult(process.ExitCode == 0, output);
    }
}
