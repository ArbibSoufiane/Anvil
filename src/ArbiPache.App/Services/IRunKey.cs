namespace ArbiPache.App.Services;

/// <summary>The "ArbiPache" value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
public interface IRunKey
{
    bool IsSet();
    void Set(string command);
    void Remove();
}
