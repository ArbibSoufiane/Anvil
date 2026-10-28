using Microsoft.Win32;

namespace Anvil.App.Services;

public sealed class RegistryRunKey : IRunKey
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Anvil";

    public bool IsSet()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunPath);
        return key?.GetValue(ValueName) is string;
    }

    public void Set(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunPath);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
