using ArbiPache.App.ViewModels;
using ArbiPache.App.Views;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace ArbiPache.App.Shell;

/// <summary>Maps each page (its view model, or the <see cref="AppPage"/> itself) to its view, creating each view once.</summary>
public sealed class PageViewLocator : IDataTemplate
{
    private readonly Dictionary<AppPage, Control> _views = [];

    public bool Match(object? data) => data is AppPage or DashboardViewModel or ApacheViewModel;

    public Control Build(object? param)
    {
        var page = param switch
        {
            DashboardViewModel => AppPage.Dashboard,
            ApacheViewModel => AppPage.Apache,
            _ => (AppPage)param!,
        };
        if (!_views.TryGetValue(page, out var view))
        {
            view = page switch
            {
                AppPage.Dashboard => new DashboardView(),
                AppPage.Sites => new SitesView(),
                AppPage.Apache => new ApacheView(),
                AppPage.Php => new PhpView(),
                AppPage.Database => new DatabaseView(),
                AppPage.Mode => new ModeView(),
                AppPage.Logs => new LogsView(),
                AppPage.Settings => new SettingsView(),
                _ => throw new ArgumentOutOfRangeException(nameof(param), page, null),
            };
            _views[page] = view;
        }
        return view;
    }
}
