using Microsoft.Maui.Networking;
using VusosConnectivity = Vusos.Core.Platform.IConnectivity;

namespace Vusos.App.Services;

/// <summary>
/// <see cref="VusosConnectivity"/> nad <see cref="Connectivity"/> z MAUI. Brak sieci = <see cref="NetworkAccess.None"/>;
/// stany Unknown / Local / ConstrainedInternet traktujemy jako „online” (żądanie i tak może się nie powieść → cache).
/// </summary>
public sealed class MauiConnectivity : VusosConnectivity, IDisposable
{
    private readonly Microsoft.Maui.Networking.IConnectivity _connectivity = Connectivity.Current;

    public MauiConnectivity()
    {
        _connectivity.ConnectivityChanged += OnConnectivityChanged;
    }

    public bool IsOnline => IsReachable(_connectivity.NetworkAccess);

    public bool IsMetered => _connectivity.ConnectionProfiles.Contains(ConnectionProfile.Cellular);

    public event EventHandler<bool>? OnlineChanged;

    private static bool IsReachable(NetworkAccess access) => access is NetworkAccess.Internet or NetworkAccess.ConstrainedInternet;

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e) =>
        OnlineChanged?.Invoke(this, IsReachable(e.NetworkAccess));

    public void Dispose() => _connectivity.ConnectivityChanged -= OnConnectivityChanged;
}
