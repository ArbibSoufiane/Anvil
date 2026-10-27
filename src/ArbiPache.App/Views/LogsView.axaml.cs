using System.Collections.Specialized;
using ArbiPache.App.ViewModels;
using Avalonia;
using Avalonia.Controls;

namespace ArbiPache.App.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _vm;

    public LogsView() => InitializeComponent();

    // While following, keep the newest line in view.
    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null) _vm.Lines.CollectionChanged -= OnLinesChanged;
        _vm = DataContext as LogsViewModel;
        if (_vm is not null) _vm.Lines.CollectionChanged += OnLinesChanged;
        base.OnDataContextChanged(e);
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm is not { Follow: true } || _vm.Lines.Count == 0 || e.Action != NotifyCollectionChangedAction.Add) return;
        LogList.ScrollIntoView(_vm.Lines.Count - 1);
    }
}
