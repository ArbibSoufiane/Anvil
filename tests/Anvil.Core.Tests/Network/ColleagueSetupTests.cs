using System.Net;
using Anvil.Core.Network;

namespace Anvil.Core.Tests.Network;

public sealed class ColleagueSetupTests
{
    [Fact]
    public void Windows_command() => Assert.Equal("Add-DnsClientNrptRule -Namespace \".arbib.pc\" -NameServers 192.168.1.5",
        ColleagueSetup.WindowsCommand("arbib.pc", IPAddress.Parse("192.168.1.5")));

    [Fact]
    public void Mac_command() => Assert.Equal("sudo mkdir -p /etc/resolver && echo \"nameserver 192.168.1.5\" | sudo tee /etc/resolver/arbib.pc",
        ColleagueSetup.MacCommand("arbib.pc", IPAddress.Parse("192.168.1.5")));

    [Fact]
    public void Windows_undo_command() => Assert.Equal("Get-DnsClientNrptRule | Where-Object Namespace -eq '.arbib.pc' | Remove-DnsClientNrptRule -Force",
        ColleagueSetup.WindowsUndoCommand("arbib.pc"));
}
