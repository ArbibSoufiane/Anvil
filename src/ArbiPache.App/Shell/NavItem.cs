using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArbiPache.App.Shell;

public sealed partial class NavItem(AppPage page, string label, string iconPath) : ObservableObject
{
    public AppPage Page { get; } = page;
    public string Label { get; } = label;
    /// <summary>Line icon on a 24x24 grid, from the mockup's SVGs.</summary>
    public Geometry Icon { get; } = StreamGeometry.Parse(iconPath);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
