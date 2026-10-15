using HADA.Core.Abstractions;
using HADA.Platform.Linux;

namespace HADA.Tests.Platform;

/// <summary>Wi-Fi and the connected networks on Linux, read from what NetworkManager and the kernel say.</summary>
public sealed class LinuxNetworkTests : IDisposable
{
    private readonly string _sysNet = Path.Combine(Path.GetTempPath(), "hada-net-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_sysNet))
        {
            Directory.Delete(_sysNet, recursive: true);
        }
    }

    [Theory]
    [InlineData("a:b:c", new[] { "a", "b", "c" })]
    [InlineData(@"yes:Home\:Net:72", new[] { "yes", "Home:Net", "72" })]
    [InlineData(@"no:Back\\slash:40", new[] { "no", @"Back\slash", "40" })]
    [InlineData("::", new[] { "", "", "" })]
    public void Nmcli_fields_are_split_with_their_escapes_undone(string line, string[] fields) =>
        Assert.Equal(fields, LinuxNetwork.SplitTerse(line));

    [Fact]
    public void The_wifi_network_connected_to_is_the_active_one()
    {
        const string list = "no:Neighbour:81\nyes:Dom\\:5G:64\nno::30\n";

        Assert.Equal(new WifiStatus(true, "Dom:5G", 64), LinuxNetwork.ParseWifiList(list));
        Assert.Equal(new WifiStatus(true, null, null), LinuxNetwork.ParseWifiList("no:Neighbour:81\n"));
    }

    [Fact]
    public void Without_a_wifi_device_there_is_no_wifi()
    {
        var network = new LinuxNetwork((_, arguments) => new ToolResult(0, "ethernet\nloopback\n", string.Empty), _sysNet);

        Assert.Equal(default, network.ReadWifi());
    }

    [Fact]
    public void With_networkmanager_the_wifi_is_read_from_it()
    {
        var network = new LinuxNetwork(
            (_, arguments) => arguments.Contains("TYPE")
                ? new ToolResult(0, "wifi\nethernet\nloopback\n", string.Empty)
                : new ToolResult(0, "yes:Dom:58\n", string.Empty),
            _sysNet);

        Assert.Equal(new WifiStatus(true, "Dom", 58), network.ReadWifi());
    }

    [Fact]
    public void Without_networkmanager_the_kernel_and_iw_tell_it()
    {
        Directory.CreateDirectory(Path.Combine(_sysNet, "wlp2s0", "wireless"));
        Directory.CreateDirectory(Path.Combine(_sysNet, "enp1s0"));
        var network = new LinuxNetwork(
            (tool, arguments) => tool == "iw" && arguments[1] == "wlp2s0"
                ? new ToolResult(0, "Connected to 11:22:33:44:55:66 (on wlp2s0)\n\tSSID: Dom\n\tfreq: 5180\n\tsignal: -61 dBm\n", string.Empty)
                : null,
            _sysNet);

        Assert.Equal(new WifiStatus(true, "Dom", 78), network.ReadWifi());
        Assert.Equal(["Dom"], network.ConnectedNetworks());
    }

    [Fact]
    public void Iw_says_when_it_is_not_connected() =>
        Assert.Null(LinuxNetwork.ParseIwLink("Not connected.\n"));

    [Fact]
    public void The_connected_networks_are_the_active_connections_but_the_loopback()
    {
        const string active = "Dom 5G:802-11-wireless\nWired connection 1:802-3-ethernet\nlo:loopback\nWork\\: VPN:vpn\n";

        Assert.Equal(["Dom 5G", "Wired connection 1", "Work: VPN"], LinuxNetwork.ParseActiveConnections(active));
    }
}
