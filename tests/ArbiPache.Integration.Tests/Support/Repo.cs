namespace ArbiPache.Integration.Tests.Support;

internal static class Repo
{
    public static string Root { get; } = FindRoot();

    public static string Path(string relative) =>
        System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "ArbiPache.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("ArbiPache.sln not found above the test output.");
    }
}
