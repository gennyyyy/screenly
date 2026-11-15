using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Screenly.Core.Discovery;

// Minimal mDNS responder for _screenly._tcp.local. service discovery.
// Announces the service via multicast (224.0.0.251:5353) and responds to queries.
public sealed class MdnsBroadcaster : IDisposable
{
    private const string ServiceType = "_screenly._tcp.local.";
    private const string MulticastIp = "224.0.0.251";
    private const int Port = 5353;

    private readonly string _hostName;
    private readonly int _servicePort;
    private readonly IPAddress _localIp;
    private UdpClient? _udp;
    private Thread? _listenThread;
    private Thread? _announceThread;
    private volatile bool _running;
    private readonly Dictionary<string, DateTime> _lastResponse = new();
    private readonly object _rateLock = new();

    public MdnsBroadcaster(int servicePort)
    {
        _servicePort = servicePort;
        _hostName = Dns.GetHostName().ToLowerInvariant().Replace(" ", "-") + ".local.";
        _localIp = GetLocalIp();
    }

    public void Start()
    {
        _running = true;
        Console.WriteLine("mDNS advertising");
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        try { _udp.JoinMulticastGroup(IPAddress.Parse(MulticastIp)); } catch { }
        _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "mdns-listen" };
        _listenThread.Start();
        _announceThread = new Thread(AnnounceLoop) { IsBackground = true, Name = "mdns-announce" };
        _announceThread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _udp?.Close(); } catch { }
    }

    public void Dispose() => Stop();

    private void ListenLoop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                var data = _udp!.Receive(ref remote);
                if (data.Length < 12) continue;
                // Ignore our own multicast echo.
                if (remote.Address.Equals(_localIp)) continue;
                // Only answer queries (QR bit 0), never responses.
                if (!IsQuery(data)) continue;
                // Check if any question contains "_screenly"
                if (!ContainsServiceQuery(data)) continue;
                // Rate-limit: one response per remote IP per 1s.
                lock (_rateLock)
                {
                    if (_lastResponse.TryGetValue(remote.Address.ToString(), out var last)
                        && (DateTime.UtcNow - last).TotalSeconds < 1) continue;
                    _lastResponse[remote.Address.ToString()] = DateTime.UtcNow;
                }
                var resp = BuildResponse();
                var ep = new IPEndPoint(IPAddress.Parse(MulticastIp), Port);
                _udp.Send(resp, resp.Length, ep);
            }
            catch (SocketException) when (!_running) { break; }
            catch { }
        }
    }

    private void AnnounceLoop()
    {
        var ep = new IPEndPoint(IPAddress.Parse(MulticastIp), Port);
        int count = 0;
        while (_running)
        {
            try
            {
                var resp = BuildResponse();
                _udp?.Send(resp, resp.Length, ep);
            }
            catch { }
            count++;
            // Announce more frequently at start
            Thread.Sleep(count < 5 ? 2000 : 30000);
        }
    }

    private static bool IsQuery(byte[] data) => (data[2] & 0x80) == 0;

    private bool ContainsServiceQuery(byte[] data)
    {
        try
        {
            // Parse question count
            int qdcount = (data[4] << 8) | data[5];
            int idx = 12;
            for (int q = 0; q < qdcount && idx < data.Length; q++)
            {
                var name = ReadName(data, idx, out idx);
                idx += 4; // QTYPE + QCLASS
                if (name.Contains("_screenly", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    private byte[] BuildResponse()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        // Header: ID=0, flags=0x8400 (response, authoritative), counts
        w.Write((ushort)0);       // ID
        w.Write((ushort)0x8400);  // flags: response
        w.Write((ushort)0);       // QDCOUNT
        w.Write((ushort)3);       // ANCOUNT: PTR, SRV, A (plus TXT in additional)
        w.Write((ushort)0);       // NSCOUNT
        w.Write((ushort)1);       // ARCOUNT (TXT)

        // Answer 1: PTR record
        WriteName(w, ServiceType);
        w.Write((ushort)12);      // TYPE PTR
        w.Write((ushort)1);       // CLASS IN
        w.Write((uint)120);       // TTL
        var instanceName = $"Screenly.{ServiceType}";
        var rdata = EncodeName(instanceName);
        w.Write((ushort)rdata.Length);
        w.Write(rdata);

        // Answer 2: SRV record for instance
        WriteName(w, instanceName);
        w.Write((ushort)33);      // TYPE SRV
        w.Write((ushort)1);       // CLASS IN
        w.Write((uint)120);       // TTL
        // SRV rdata: priority(2) weight(2) port(2) target(name)
        var targetBytes = EncodeName(_hostName);
        var srvRdata = new byte[6 + targetBytes.Length];
        srvRdata[0] = 0; srvRdata[1] = 0; // priority
        srvRdata[2] = 0; srvRdata[3] = 0; // weight
        srvRdata[4] = (byte)(_servicePort >> 8);
        srvRdata[5] = (byte)(_servicePort & 0xFF);
        Buffer.BlockCopy(targetBytes, 0, srvRdata, 6, targetBytes.Length);
        w.Write((ushort)srvRdata.Length);
        w.Write(srvRdata);

        // Answer 3: A record for host
        WriteName(w, _hostName);
        w.Write((ushort)1);       // TYPE A
        w.Write((ushort)1);       // CLASS IN
        w.Write((uint)120);       // TTL
        var ipBytes = _localIp.GetAddressBytes();
        w.Write((ushort)4);
        w.Write(ipBytes);

        // Additional: TXT record
        WriteName(w, instanceName);
        w.Write((ushort)16);      // TYPE TXT
        w.Write((ushort)1);       // CLASS IN
        w.Write((uint)120);       // TTL
        var txt = System.Text.Encoding.UTF8.GetBytes("path=/");
        var txtRdata = new byte[1 + txt.Length];
        txtRdata[0] = (byte)txt.Length;
        Buffer.BlockCopy(txt, 0, txtRdata, 1, txt.Length);
        w.Write((ushort)txtRdata.Length);
        w.Write(txtRdata);

        return ms.ToArray();
    }

    private static void WriteName(BinaryWriter w, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(label);
            w.Write((byte)bytes.Length);
            w.Write(bytes);
        }
        w.Write((byte)0);
    }

    private static byte[] EncodeName(string name)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        WriteName(w, name);
        return ms.ToArray();
    }

    private static string ReadName(byte[] data, int startIdx, out int endIdx)
    {
        var sb = new StringBuilder();
        int idx = startIdx;
        while (idx < data.Length && data[idx] != 0)
        {
            int len = data[idx];
            if (len > 63) { idx++; continue; } // compression pointer not handled
            idx++;
            sb.Append(System.Text.Encoding.UTF8.GetString(data, idx, len));
            sb.Append('.');
            idx += len;
        }
        idx++; // skip terminating 0
        endIdx = idx;
        return sb.ToString();
    }

    private static IPAddress GetLocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            return (s.LocalEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
        }
        catch { return IPAddress.Loopback; }
    }
}
