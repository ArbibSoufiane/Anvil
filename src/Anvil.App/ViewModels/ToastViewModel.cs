using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anvil.App.ViewModels;

/// <summary>
/// The note in the window's bottom-right corner: green when all is well, red when not. It closes by itself after a few
/// seconds, or with ×; a newer note replaces it.
/// </summary>
public sealed partial class ToastViewModel(Func<TimeSpan, CancellationToken, Task>? delay = null) : ObservableObject
{
    public static readonly TimeSpan Shown = TimeSpan.FromSeconds(6);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private CancellationTokenSource? _hide;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial string? Text { get; set; }

    [ObservableProperty]
    public partial bool Ok { get; set; }

    public bool IsVisible => Text is not null;

    /// <summary>Shows <paramref name="text"/>; <paramref name="stay"/> keeps it until replaced or closed.</summary>
    public void Show(string text, bool ok, bool stay = false)
    {
        _hide?.Cancel();
        Text = text;
        Ok = ok;
        if (!stay) _ = HideLaterAsync(_hide = new CancellationTokenSource());
    }

    [RelayCommand]
    private void Close()
    {
        _hide?.Cancel();
        Text = null;
    }

    private async Task HideLaterAsync(CancellationTokenSource hide)
    {
        try
        {
            await _delay(Shown, hide.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!hide.IsCancellationRequested) Text = null;
    }
}
