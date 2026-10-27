using ArbiPache.Core.Hosts;

namespace ArbiPache.Core.Tests.Hosts;

public sealed class HostsFileTests
{
    private const string Stock = "# Copyright (c) 1993-2009 Microsoft Corp.\r\n#\r\n#\t127.0.0.1       localhost\r\n";

    [Fact]
    public void Block_added_replaced_and_removed()
    {
        var added = HostsFile.ApplyBlock(Stock, ["shop.test", "blog.test"]);

        Assert.Equal(Stock
            + "# >>> ArbiPache >>>\r\n127.0.0.1 shop.test\r\n::1 shop.test\r\n127.0.0.1 blog.test\r\n::1 blog.test\r\n# <<< ArbiPache <<<\r\n",
            added);
        Assert.Equal(["shop.test", "blog.test"], HostsFile.ManagedDomains(added));

        var replaced = HostsFile.ApplyBlock(added, ["new.test"]);
        Assert.Equal(Stock + "# >>> ArbiPache >>>\r\n127.0.0.1 new.test\r\n::1 new.test\r\n# <<< ArbiPache <<<\r\n", replaced);

        Assert.Equal(Stock, HostsFile.ApplyBlock(replaced, []));
        Assert.Empty(HostsFile.ManagedDomains(Stock));
    }

    [Fact]
    public void Other_lines_untouched()
    {
        var hosts = Stock + "# >>> ArbiPache >>>\r\n127.0.0.1 old.test\r\n::1 old.test\r\n# <<< ArbiPache <<<\r\n10.0.0.9 nas.lan # mine\r\n";

        var updated = HostsFile.ApplyBlock(hosts, ["shop.test"]);

        Assert.Equal(Stock + "# >>> ArbiPache >>>\r\n127.0.0.1 shop.test\r\n::1 shop.test\r\n# <<< ArbiPache <<<\r\n10.0.0.9 nas.lan # mine\r\n", updated);
        Assert.Equal(Stock + "10.0.0.9 nas.lan # mine\r\n", HostsFile.ApplyBlock(updated, []));
    }

    [Fact]
    public void Crlf_preserved()
    {
        Assert.DoesNotContain("\r\n", HostsFile.ApplyBlock("127.0.0.1 router\n", ["shop.test"]).Replace("\r\n", "!"));
        Assert.Equal("127.0.0.1 router\n# >>> ArbiPache >>>\n127.0.0.1 shop.test\n::1 shop.test\n# <<< ArbiPache <<<\n",
            HostsFile.ApplyBlock("127.0.0.1 router\n", ["shop.test"]));
        Assert.Equal("127.0.0.1 router\r\n# >>> ArbiPache >>>\r\n127.0.0.1 shop.test\r\n::1 shop.test\r\n# <<< ArbiPache <<<\r\n",
            HostsFile.ApplyBlock("127.0.0.1 router", ["shop.test"]));
    }

    [Fact]
    public void Empty_hosts_file_gets_only_the_block() =>
        Assert.Equal("# >>> ArbiPache >>>\r\n127.0.0.1 shop.test\r\n::1 shop.test\r\n# <<< ArbiPache <<<\r\n", HostsFile.ApplyBlock("", ["shop.test"]));

    [Fact]
    public void Domains_are_lower_cased_once() =>
        Assert.Equal(["shop.test"], HostsFile.ManagedDomains(HostsFile.ApplyBlock(Stock, ["Shop.TEST", "shop.test"])));

    // ArbiPache 3 wrote "127.0.0.1<TAB>domain" lines between its own markers.
    private const string V3Block =
        "# >>> ApacheManager >>>\r\n127.0.0.1\tafaq.test\r\n::1\tafaq.test\r\n127.0.0.1\told.test\r\n::1\told.test\r\n# <<< ApacheManager <<<\r\n";

    [Fact]
    public void Tab_separated_lines_are_read() =>
        Assert.Equal(["shop.test"], HostsFile.ManagedDomains("# >>> ArbiPache >>>\r\n127.0.0.1\tshop.test\r\n# <<< ArbiPache <<<\r\n"));

    [Fact]
    public void V3_domains_are_listed() =>
        Assert.Equal(["afaq.test", "old.test"], HostsFile.LegacyDomains(Stock + V3Block));

    [Fact]
    public void V3_lines_for_domains_now_managed_are_removed()
    {
        var hosts = HostsFile.ApplyBlock(Stock + V3Block, ["afaq.test"]);

        Assert.Equal(Stock
            + "# >>> ApacheManager >>>\r\n127.0.0.1\told.test\r\n::1\told.test\r\n# <<< ApacheManager <<<\r\n"
            + "# >>> ArbiPache >>>\r\n127.0.0.1 afaq.test\r\n::1 afaq.test\r\n# <<< ArbiPache <<<\r\n", hosts);
    }

    [Fact]
    public void V3_block_removed_once_all_its_domains_moved()
    {
        var hosts = HostsFile.ApplyBlock(Stock + V3Block, ["afaq.test", "old.test"]);

        Assert.DoesNotContain("ApacheManager", hosts);
        Assert.Empty(HostsFile.LegacyDomains(hosts));
        Assert.Equal(["afaq.test", "old.test"], HostsFile.ManagedDomains(hosts));
    }
}
