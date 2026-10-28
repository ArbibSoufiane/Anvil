namespace Anvil.App.Services;

public interface ITextClipboard
{
    Task SetTextAsync(string text);
}
