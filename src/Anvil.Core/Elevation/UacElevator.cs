using System.ComponentModel;
using System.Diagnostics;
using Anvil.Core.IO;
using Anvil.Core.Paths;

namespace Anvil.Core.Elevation;

/// <summary>
/// Writes the request to data\tmp, starts Anvil.Elevator.exe through UAC ("runas"), waits for it and reads the
/// result it wrote beside the request. Both files are deleted afterwards.
/// </summary>
public sealed class UacElevator(AppPaths paths) : IElevator
{
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public async Task<ElevatedResult> RunAsync(ElevatedRequest r, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Tmp);
        var request = Path.Combine(paths.Tmp, $"elevate-{Guid.NewGuid():N}.json");
        var result = request + ".result.json";
        try
        {
            File.WriteAllText(request, ElevatedJson.Serialize(r));
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(paths.Root, "Anvil.Elevator.exe"))
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--request \"{request}\"",
            });
            if (process is null) return new ElevatedResult(false, "The administrator helper did not start.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new ElevatedResult(false, "The administrator helper did not finish within two minutes.");
            }

            return File.Exists(result) && JsonFiles.Read<ElevatedResult>(result) is { } outcome
                ? outcome
                : new ElevatedResult(false, "The administrator helper did not report a result.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ElevatedResult(false, "Permission was not granted.");
        }
        finally
        {
            TryDelete(request);
            TryDelete(result);
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
