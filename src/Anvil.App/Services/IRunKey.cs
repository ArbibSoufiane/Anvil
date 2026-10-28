namespace Anvil.App.Services;

/// <summary>The "Anvil" value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
public interface IRunKey
{
    bool IsSet();
    void Set(string command);
    void Remove();
}
