namespace Anvil.Core.Settings;

/// <summary>Starting values derived from the machine's core count.</summary>
public static class Suggest
{
    public static int Threads(int cores) => Math.Clamp(cores * 16, 64, 1920);

    public static int PhpWorkers(int cores) => Math.Max(2, cores / 2);
}
