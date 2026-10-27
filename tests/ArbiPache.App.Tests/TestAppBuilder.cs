using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(ArbiPache.App.Tests.TestAppBuilder))]

namespace ArbiPache.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
