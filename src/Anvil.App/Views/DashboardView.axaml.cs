using Anvil.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Anvil.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    private async void OnCopyNetworkUrl(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DashboardViewModel { NetworkUrl: { } url } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(url);
    }
}
