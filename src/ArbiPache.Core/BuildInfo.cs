using System.Reflection;

namespace ArbiPache.Core;

/// <summary>The application version, read from the assembly so Directory.Build.props stays the only source.</summary>
public static class BuildInfo
{
    public static string Version { get; } = Read();

    private static string Read()
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
