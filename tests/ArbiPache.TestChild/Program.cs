// Helper process for supervisor tests.
//   --sleep                     run until killed
//   --exit-after <ms> --code <n> wait, write a last line to stderr, exit with <n>
//   --print-env <NAME>          write the variable's value to stdout first
string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

Console.WriteLine($"child {Environment.ProcessId} started");
if (Arg("--print-env") is { } variable) Console.WriteLine($"{variable}={Environment.GetEnvironmentVariable(variable)}");
if (args.Contains("--sleep")) Thread.Sleep(Timeout.Infinite);

Thread.Sleep(int.Parse(Arg("--exit-after") ?? "0"));
var code = int.Parse(Arg("--code") ?? "0");
Console.Error.WriteLine($"child exiting with {code}");
return code;
