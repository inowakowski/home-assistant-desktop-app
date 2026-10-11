using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using HADA.Core.Abstractions;

namespace HADA.Platform.Windows.Network;

/// <summary>
/// The networks Windows says the computer is connected to, by the names it shows them under in its own
/// settings: the name of a Wi-Fi network, and "Network", "Network 2" and so on for wired ones and VPNs.
/// </summary>
/// <remarks>
/// Asked of the Network List Manager, which tells networks apart itself, a wired one by the router it leads to.
/// Unlike the WLAN API it needs no access to location, and it knows wired networks too.
/// </remarks>
public sealed class WindowsNetworkLocation : INetworkLocation, IDisposable
{
    private const int ConnectedNetworksOnly = 1;

    private static readonly Guid NetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    public WindowsNetworkLocation() => NetworkChange.NetworkAddressChanged += OnAddressChanged;

    public event EventHandler? Changed;

    public IReadOnlyList<string> ConnectedNetworks => Read();

    /// <summary>The names of the networks connected right now; empty when Windows cannot be asked.</summary>
    public static IReadOnlyList<string> Read()
    {
        object? manager = null;
        try
        {
            if (Type.GetTypeFromCLSID(NetworkListManager) is not { } type)
            {
                return [];
            }

            manager = Activator.CreateInstance(type);
            var names = new List<string>();

            // Late bound: the two calls needed are not worth declaring three interfaces for.
            foreach (var network in ((dynamic)manager!).GetNetworks(ConnectedNetworksOnly))
            {
                try
                {
                    if ((string?)network.GetName() is { Length: > 0 } name && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(network);
                }
            }

            return names;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException
            or MissingMethodException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Reflection.TargetInvocationException)
        {
            // The service that knows is not running, or not there: no network can be named.
            return [];
        }
        finally
        {
            if (manager is not null)
            {
                Marshal.ReleaseComObject(manager);
            }
        }
    }

    public void Dispose() => NetworkChange.NetworkAddressChanged -= OnAddressChanged;

    private void OnAddressChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
