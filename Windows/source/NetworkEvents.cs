using System.Net.NetworkInformation;

namespace CampusConnect;

// Windows already knows when the network address or connection level changes, so the
// tray title and the login state can follow immediately instead of waiting for the
// 20 second heartbeat. The heartbeat stays as the slow safety net.
internal sealed class NetworkEvents : IDisposable
{
    private readonly Action changed;
    private int signaled;

    public NetworkEvents(Action changed)
    {
        this.changed = changed;
        try {
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        } catch (NetworkInformationException) { /* no adapters yet: the heartbeat still runs */ }
    }

    public void Dispose()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Signal();
    private void OnNetworkChanged(object? sender, EventArgs e) => Signal();

    // These events can fire several times for one change; one pending update is enough.
    private void Signal()
    {
        if (Interlocked.Exchange(ref signaled, 1) == 1) return;
        try { changed(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        finally { Volatile.Write(ref signaled, 0); }
    }
}
