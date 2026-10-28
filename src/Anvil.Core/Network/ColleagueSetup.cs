using System.Net;

namespace Anvil.Core.Network;

/// <summary>
/// The one-time command a colleague runs so their PC asks this one about every name under the network domain. Windows
/// sends only that domain to this PC (a Name Resolution Policy Table rule); a Mac does the same through /etc/resolver.
/// </summary>
public static class ColleagueSetup
{
    public static string WindowsCommand(string domain, IPAddress ip) => $"Add-DnsClientNrptRule -Namespace \".{domain}\" -NameServers {ip}";

    public static string MacCommand(string domain, IPAddress ip) =>
        $"sudo mkdir -p /etc/resolver && echo \"nameserver {ip}\" | sudo tee /etc/resolver/{domain}";

    public static string WindowsUndoCommand(string domain) =>
        $"Get-DnsClientNrptRule | Where-Object Namespace -eq '.{domain}' | Remove-DnsClientNrptRule -Force";
}
