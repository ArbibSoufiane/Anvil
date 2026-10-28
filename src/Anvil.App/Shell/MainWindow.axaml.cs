using Avalonia.Controls;

namespace Anvil.App.Shell;

public partial class MainWindow : Window
{
    private readonly ShellViewModel? _vm;

    public MainWindow() => InitializeComponent();

    public MainWindow(ShellViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        vm.ActivationRequested += (_, _) => ShowAndActivate();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The close button hides to the tray; Quit (application shutdown) still closes.
        if (_vm is { CloseToTray: true } && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
