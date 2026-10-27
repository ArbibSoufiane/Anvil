using ArbiPache.App.Views;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace ArbiPache.App.Shell;

/// <summary>Maps each <see cref="AppPage"/> to its view, creating each view once.</summary>
public sealed class PageViewLocator : IDataTemplate
{
    private readonly Dictionary<AppPage, Control> _views = [];

    public bool Match(object? data) => data is AppPage;

    public Control Build(object? param)
    {
        var page = (AppPage)param!;
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
