using System.Diagnostics;
using System.Text;

namespace Anvil.Core.Redis;

/// <summary>The real wsl.exe in System32.</summary>
public sealed class WslCli : IWsl
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    public bool Exists => File.Exists(_exe);

    public async Task<WslResult> RunAsync(IReadOnlyList<string> args, string? stdin, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
    {
        using var process = Process.Start(Start(args, stdin is not null, redirectOutput: true))!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var output = new StringBuilder();
        try
        {
            var stdout = PumpAsync(process.StandardOutput.BaseStream, output, onLine, cts.Token);
            var stderr = PumpAsync(process.StandardError.BaseStream, output, onLine, cts.Token);
            if (stdin is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(Utf8.GetBytes(stdin), cts.Token);
                process.StandardInput.Close();
            }
            await Task.WhenAll(stdout, stderr);
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            lock (output) output.AppendLine($"wsl.exe gave no answer within {timeout.TotalMinutes:0} minutes.");
            return new WslResult(-1, output.ToString());
        }
        catch
        {
            Kill(process);
            throw;
        }
        return new WslResult(process.ExitCode, output.ToString());
    }

    public void StartDetached(IReadOnlyList<string> args, string stdin)
    {
        using var process = Process.Start(Start(args, redirectInput: true, redirectOutput: false))!;
        process.StandardInput.BaseStream.Write(Utf8.GetBytes(stdin));
        process.StandardInput.Close();
    }

    private ProcessStartInfo Start(IReadOnlyList<string> args, bool redirectInput, bool redirectOutput)
    {
        var psi = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
        };
        // wsl.exe's own messages are UTF-16 unless this is set; older versions ignore it, which Decode handles.
        psi.Environment["WSL_UTF8"] = "1";
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    // Reads raw bytes: wsl.exe's own text may be UTF-16 while Linux programs print UTF-8.
    private static async Task PumpAsync(Stream stream, StringBuilder output, Action<string>? onLine, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            var bytes = buffer.ToArray();
            var end = Array.LastIndexOf(bytes, (byte)'\n');
            if (end < 0) continue;
            // A UTF-16 newline is followed by a zero byte.
            if (end + 1 < bytes.Length && bytes[end + 1] == 0) end++;
            Emit(Decode(bytes.AsSpan(0, end + 1)), output, onLine);
            buffer.SetLength(0);
            buffer.Write(bytes, end + 1, bytes.Length - end - 1);
        }
        if (buffer.Length > 0) Emit(Decode(buffer.ToArray()), output, onLine);
    }

    private static void Emit(string text, StringBuilder output, Action<string>? onLine)
    {
        lock (output) output.Append(text);
        if (onLine is null) return;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0) onLine(trimmed);
        }
    }

    /// <summary>UTF-16 when every other byte is zero, as wsl.exe's own messages are; UTF-8 otherwise.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2)
        {
            var zeros = 0;
            for (var i = 1; i < bytes.Length; i += 2)
                if (bytes[i] == 0) zeros++;
            if (zeros * 4 >= bytes.Length) return Encoding.Unicode.GetString(bytes).Replace("\0", "", StringComparison.Ordinal);
        }
        return Utf8.GetString(bytes);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
