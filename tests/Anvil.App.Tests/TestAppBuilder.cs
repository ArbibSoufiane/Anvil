using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Anvil.App.Tests.TestAppBuilder))]

namespace Anvil.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
