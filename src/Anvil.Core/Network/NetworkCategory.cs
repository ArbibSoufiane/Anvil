using System.Runtime.InteropServices;
using Serilog;

namespace Anvil.Core.Network;

/// <summary>Which adapters Windows has on a network it calls Public, where its firewall blocks other devices.</summary>
public static class NetworkCategory
{
    private static readonly Guid NetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
    private const int Public = 0;

    /// <returns>Adapter ids in NetworkInterface.Id form ("{GUID}"), or none when Windows would not say.</returns>
    public static IReadOnlySet<string> PublicAdapterIds() =>
        AdapterCategories().Where(c => c.Value == Public).Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each connected adapter's network category from the Network List Manager: 0 Public, 1 Private, 2 Domain. Empty when
    /// Windows would not say.
    /// </summary>
    public static IReadOnlyDictionary<string, int> AdapterCategories()
    {
        var categories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            dynamic manager = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManager, throwOnError: true)!)!;
            foreach (dynamic connection in manager.GetNetworkConnections())
            {
                // GetAdapterId returns a GUID, which IDispatch cannot carry: that one call goes through the vtable.
                var adapter = ((INetworkConnection)(object)connection).GetAdapterId();
                categories[adapter.ToString("B").ToUpperInvariant()] = (int)connection.GetNetwork().GetCategory();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warning(e, "Windows did not say which networks are Public");
            categories.Clear();
        }

        return categories;
    }

    /// <summary>netlistmgr.h's INetworkConnection, in vtable order.</summary>
    [ComImport]
    [Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object GetNetwork();

        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }

        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }

        int GetConnectivity();

        Guid GetConnectionId();

        Guid GetAdapterId();

        int GetDomainType();
    }
}
