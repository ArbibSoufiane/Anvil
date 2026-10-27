using System.Net;
using System.Net.Sockets;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Platform;
using ArbiPache.Core.Preflight;
using ArbiPache.Core.Settings;
using Microsoft.Win32;

namespace ArbiPache.Core.Tests.Preflight;

public sealed class PreflightTests
{
    private static readonly AppPaths Paths = new(@"D:\ArbiPache");
    private static readonly string IisNetsh = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Preflight", "Fixtures", "netsh-http-servicestate-80.txt"));
    private const string EmptyNetsh = "Snapshot of HTTP service state (Request Queue View): \r\n----- \r\n";
    private const long Plenty = 50L * 1024 * 1024 * 1024;

    private static VcRedistCheck Vc(bool installed) => new(new FakeRegistry(installed));

    private static ArbiPache.Core.Preflight.Preflight Make(IPortProbe probe, string netsh = EmptyNetsh, bool vc = true, long free = Plenty) =>
        new(probe, () => netsh, Vc(vc), Paths, _ => free);

    [Fact]
    public void HttpSys_parser_finds_urls_on_port()
    {
        Assert.Equal(["HTTP://*:80/"], HttpSysParser.RegistrationsOnPort(IisNetsh, 80));
        Assert.Equal(["HTTPS://*:443/"], HttpSysParser.RegistrationsOnPort(IisNetsh, 443));
        Assert.Equal(["HTTP://[FE80::1]:8080/APP/"], HttpSysParser.RegistrationsOnPort(IisNetsh, 8080));
        Assert.Empty(HttpSysParser.RegistrationsOnPort(IisNetsh, 8443));
        Assert.Empty(HttpSysParser.RegistrationsOnPort(EmptyNetsh, 80));
    }

    [Fact]
    public void HttpSys_registration_on_80_is_a_finding()
    {
        var findings = Make(new FakeProbe(), IisNetsh).Run(new AppSettings());

        var finding = Assert.Single(findings, f => f.Id == "http-sys-80");
        Assert.Equal(Severity.Blocking, finding.Severity);
        Assert.Contains("HTTP://*:80/", finding.Message);
        Assert.Contains(findings, f => f.Id == "http-sys-443");
    }

    [Fact]
    public void Port_held_by_our_httpd_is_not_a_finding()
    {
        var ours = new PortHolder(80, 1234, "httpd", @"D:\ArbiPache\runtime\apache\2.4.69\bin\httpd.exe");

        var findings = Make(new FakeProbe(ours)).Run(new AppSettings());

        Assert.Empty(findings);
    }

    [Fact]
    public void Port_held_by_another_httpd_is_blocking()
    {
        var xampp = new PortHolder(80, 1234, "httpd", @"C:\xampp\apache\bin\httpd.exe");

        var finding = Assert.Single(Make(new FakeProbe(xampp)).Run(new AppSettings()));

        Assert.Equal("port-80", finding.Id);
        Assert.Equal("Port 80 is used by httpd (PID 1234).", finding.Message);
    }

    [Fact]
    public void Port_held_by_other_process_is_blocking_with_name()
    {
        var skype = new PortHolder(443, 777, "Skype", @"C:\Program Files\Skype\Skype.exe");

        var finding = Assert.Single(Make(new FakeProbe(skype)).Run(new AppSettings()));

        Assert.Equal("port-443", finding.Id);
        Assert.Equal(Severity.Blocking, finding.Severity);
        Assert.Equal("Port 443 is used by Skype (PID 777).", finding.Message);
        Assert.Equal("Close Skype, or change the HTTPS port in Settings.", finding.Fix);
    }

    [Fact]
    public void Port_held_by_http_sys_names_iis()
    {
        var system = new PortHolder(80, 4, "System", null);

        var finding = Assert.Single(Make(new FakeProbe(system)).Run(new AppSettings()));

        Assert.Equal("Port 80 is used by Windows HTTP.SYS (PID 4), usually IIS (W3SVC).", finding.Message);
        Assert.Equal("Stop the World Wide Web Publishing service, or change the HTTP port in Settings.", finding.Fix);
    }

    [Fact]
    public void Custom_ports_are_checked()
    {
        var other = new PortHolder(8080, 99, "java", null);

        var finding = Assert.Single(Make(new FakeProbe(other)).Run(new AppSettings { HttpPort = 8080 }));

        Assert.Equal("port-8080", finding.Id);
    }

    [Fact]
    public void Missing_vcredist_is_blocking()
    {
        var finding = Assert.Single(Make(new FakeProbe(), vc: false).Run(new AppSettings()));

        Assert.Equal("vcredist", finding.Id);
        Assert.Equal(Severity.Blocking, finding.Severity);
        Assert.Contains(VcRedistCheck.DownloadPage.ToString(), finding.Fix);
    }

    [Fact]
    public void Low_disk_is_warning()
    {
        var finding = Assert.Single(Make(new FakeProbe(), free: 500L * 1024 * 1024).Run(new AppSettings()));

        Assert.Equal("disk-space", finding.Id);
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    [Fact]
    public void Real_probe_finds_a_listening_socket_and_its_process()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var holder = new PortProbe().HolderOf(port);

        Assert.NotNull(holder);
        Assert.Equal(Environment.ProcessId, holder.Pid);
        Assert.Equal(Environment.ProcessPath, holder.ExePath, ignoreCase: true);
    }

    [Fact]
    public void Real_probe_returns_null_for_a_free_port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Null(new PortProbe().HolderOf(port));
    }

    private sealed class FakeProbe(params PortHolder[] holders) : IPortProbe
    {
        public PortHolder? HolderOf(int port) => holders.FirstOrDefault(h => h.Port == port);
    }

    private sealed class FakeRegistry(bool installed) : IRegistryReader
    {
        public object? GetValue(RegistryHive hive, string path, string name) => name switch
        {
            "Installed" => installed ? 1 : null,
            "Version" => "v14.51.36247.00",
            _ => null,
        };
    }
}
