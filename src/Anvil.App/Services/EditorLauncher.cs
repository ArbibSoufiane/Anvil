using System.Diagnostics;
using Anvil.Core.Platform;
using Microsoft.Win32;

namespace Anvil.App.Services;

/// <summary>
/// Opens configuration files in the editor chosen in Settings, or with "auto" in the first one found:
/// VS Code (per-user install, then on PATH), Notepad++, Notepad.
/// </summary>
/// <param name="pathVariable">The PATH to search; null reads the process environment.</param>
public sealed class EditorLauncher(Func<string> editorSetting, IRegistryReader reg, Func<string, bool> fileExists, string? pathVariable = null)
{
    public string Resolve()
    {
        var chosen = editorSetting();
        if (!string.Equals(chosen, "auto", StringComparison.OrdinalIgnoreCase) && fileExists(chosen)) return chosen;

        var vscode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe");
        if (fileExists(vscode)) return vscode;

        // code.cmd is a batch file; start the Code.exe next to its bin folder so paths are never parsed by cmd.exe.
        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bin = Path.TrimEndingDirectorySeparator(dir);
            if (!fileExists(Path.Combine(bin, "code.cmd")) || Path.GetDirectoryName(bin) is not { } install) continue;
            var exe = Path.Combine(install, "Code.exe");
            if (fileExists(exe)) return exe;
        }

        foreach (var key in new[] { @"SOFTWARE\Notepad++", @"SOFTWARE\WOW6432Node\Notepad++" })
        {
            if (reg.GetValue(RegistryHive.LocalMachine, key, "") is not string dir) continue;
            var npp = Path.Combine(dir, "notepad++.exe");
            if (fileExists(npp)) return npp;
        }
        return "notepad.exe";
    }

    public void Open(string file)
    {
        var psi = new ProcessStartInfo(Resolve()) { UseShellExecute = false };
        psi.ArgumentList.Add(file);
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Opens the folder in Explorer, or its nearest parent while it does not exist yet: the window creates nothing in data\.</summary>
    public static void OpenFolder(string folder)
    {
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(ExistingFolder(folder));
        Process.Start(psi)?.Dispose();
    }

    /// <summary>The folder, or its nearest parent that exists.</summary>
    public static string ExistingFolder(string folder)
    {
        var dir = Path.GetFullPath(folder);
        while (!Directory.Exists(dir) && Path.GetDirectoryName(dir) is { } parent) dir = parent;
        return dir;
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
}
