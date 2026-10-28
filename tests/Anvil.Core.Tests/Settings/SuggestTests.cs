using Anvil.Core.Settings;

namespace Anvil.Core.Tests.Settings;

public sealed class SuggestTests
{
    [Fact]
    public void Threads_for_16_cores_is_256() => Assert.Equal(256, Suggest.Threads(16));

    [Fact]
    public void Threads_clamped_64_to_1920()
    {
        Assert.Equal(64, Suggest.Threads(1));
        Assert.Equal(1920, Suggest.Threads(200));
    }

    [Fact]
    public void Workers_for_16_cores_is_8() => Assert.Equal(8, Suggest.PhpWorkers(16));

    [Fact]
    public void Workers_at_least_2()
    {
        Assert.Equal(2, Suggest.PhpWorkers(1));
        Assert.Equal(2, Suggest.PhpWorkers(3));
    }
}
