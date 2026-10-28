using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Anvil.App.Services;

/// <summary>The clipboard of the window <paramref name="topLevel"/> returns; does nothing before the window exists.</summary>
public sealed class AvaloniaTextClipboard(Func<TopLevel?> topLevel) : ITextClipboard
{
    public async Task SetTextAsync(string text)
    {
        if (topLevel()?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}
