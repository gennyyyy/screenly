namespace Screenly.Core.Discovery;

// mDNS broadcast of _screenly._tcp.local. on 224.0.0.251:5353.
// Implement with a library (Makaretu.Dns.Multicast) or raw UDP multicast.
public sealed class MdnsBroadcaster : IDisposable
{
    private int _port;
    public void Start(string serviceName, int port)
    {
        _port = port;
        // TODO: broadcast PTR/SRV/TXT records.
    }
    public void Stop() { }
    public void Dispose() => Stop();
}
