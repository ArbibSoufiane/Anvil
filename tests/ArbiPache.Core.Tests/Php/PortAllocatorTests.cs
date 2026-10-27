using ArbiPache.Core.Php;

namespace ArbiPache.Core.Tests.Php;

public sealed class PortAllocatorTests
{
    private static readonly Dictionary<string, int> None = [];

    [Fact]
    public void First_free_port_from_the_start_of_the_range()
    {
        Assert.Equal(9100, PortAllocator.Allocate("php85", None, _ => true));
        Assert.Equal(9102, PortAllocator.Allocate("php85", None, p => p >= 9102));
    }

    [Fact]
    public void Reuses_the_keys_existing_port()
    {
        var existing = new Dictionary<string, int> { ["php85"] = 9150 };

        Assert.Equal(9150, PortAllocator.Allocate("php85", existing, _ => true));
    }

    [Fact]
    public void Existing_port_taken_by_another_program_is_replaced()
    {
        var existing = new Dictionary<string, int> { ["php85"] = 9150 };

        Assert.Equal(9100, PortAllocator.Allocate("php85", existing, p => p != 9150));
    }

    [Fact]
    public void Never_returns_a_port_owned_by_another_key()
    {
        var existing = new Dictionary<string, int> { ["php84"] = 9100, ["php83"] = 9101 };

        Assert.Equal(9102, PortAllocator.Allocate("php85", existing, _ => true));
    }

    [Fact]
    public void Full_range_throws()
    {
        var e = Assert.Throws<InvalidOperationException>(() => PortAllocator.Allocate("php85", None, _ => false, 9100, 9102));
        Assert.Contains("9100-9102", e.Message, StringComparison.Ordinal);
    }
}
