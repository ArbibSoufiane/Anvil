using ArbiPache.App.Services;
using ArbiPache.App.Tests.Support;
using ArbiPache.Core.Platform;
using Microsoft.Win32;

namespace ArbiPache.App.Tests;

public sealed class EditorLauncherTests
{
    [Fact]
    public void Editor_choice_lives_in_its_own_file()
    {
        var file = Path.Combine(Path.GetTempPath(), "ap-editor-" + Guid.NewGuid().ToString("N"), "editor.txt");
        try
        {
            Assert.Equal("auto", new EditorChoice(file).Editor);

            new EditorChoice(file).Editor = @"C:\Tools\subl.exe";

            Assert.Equal(@"C:\Tools\subl.exe", new EditorChoice(file).Editor);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    private static readonly string VsCode = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe");
    private const string NppDir = @"C:\Program Files\Notepad++";
    private const string Npp = @"C:\Program Files\Notepad++\notepad++.exe";

    [Fact]
    public void Editor_auto_prefers_vscode_then_notepadpp_then_notepad()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { VsCode, Npp };
        var launcher = new EditorLauncher(() => "auto", new Registry(NppDir), files.Contains, "");

        Assert.Equal(VsCode, launcher.Resolve());
        files.Remove(VsCode);
        Assert.Equal(Npp, launcher.Resolve());
        files.Remove(Npp);
        Assert.Equal("notepad.exe", launcher.Resolve());
    }

    [Fact]
    public void Code_on_path_resolves_to_its_exe_not_the_cmd()
    {
        const string install = @"E:\Tools\VSCode";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $@"{install}\bin\code.cmd", $@"{install}\Code.exe", Npp };
        var launcher = new EditorLauncher(() => "auto", new Registry(NppDir), files.Contains, $@"C:\Windows;{install}\bin");

        Assert.Equal($@"{install}\Code.exe", launcher.Resolve());
    }

    [Fact]
    public void Chosen_editor_wins_when_it_exists()
    {
        const string sublime = @"C:\Program Files\Sublime Text\sublime_text.exe";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { VsCode, sublime };

        Assert.Equal(sublime, new EditorLauncher(() => sublime, new Registry(null), files.Contains, "").Resolve());
        Assert.Equal(VsCode, new EditorLauncher(() => @"C:\gone\editor.exe", new Registry(null), files.Contains, "").Resolve());
    }

    private sealed class Registry(string? nppDir) : IRegistryReader
    {
        public object? GetValue(RegistryHive hive, string path, string name) =>
            hive == RegistryHive.LocalMachine && path == @"SOFTWARE\Notepad++" && name == "" ? nppDir : null;
    }
}
