// Helper process for supervisor tests.
//   --sleep                     run until killed
//   --exit-after <ms> --code <n> wait, write a last line to stderr, exit with <n>
//   --print-env <NAME>          write the variable's value to stdout first
//   --accept-stdin              stdin is a listening socket (like php-cgi in a pool): answer each connection with "hello from <pid>"
//   (no arguments)              the same when stdin is a socket, so pool tests can launch it like php-cgi
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

Console.WriteLine($"child {Environment.ProcessId} started");
if (Arg("--print-env") is { } variable) Console.WriteLine($"{variable}={Environment.GetEnvironmentVariable(variable)}");
if (args.Contains("--sleep")) Thread.Sleep(Timeout.Infinite);

Socket? StdinSocket()
{
    try
    {
        return new Socket(new SafeSocketHandle(Native.GetStdHandle(Native.StdInput), ownsHandle: false));
    }
    catch (Exception e) when (e is SocketException or ArgumentException)
    {
        return null;
    }
}

if ((args.Contains("--accept-stdin") || args.Length == 0) && StdinSocket() is { } listener)
{
    while (true)
    {
        using var client = listener.Accept();
        client.Send(Encoding.ASCII.GetBytes($"hello from {Environment.ProcessId}\n"));
        client.Shutdown(SocketShutdown.Both);
    }
}

Thread.Sleep(int.Parse(Arg("--exit-after") ?? "0"));
var code = int.Parse(Arg("--code") ?? "0");
Console.Error.WriteLine($"child exiting with {code}");
return code;

internal static partial class Native
{
    public const int StdInput = -10;

    [LibraryImport("kernel32.dll")]
    public static partial nint GetStdHandle(int which);
}
