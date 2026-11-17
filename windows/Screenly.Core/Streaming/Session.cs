using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Screenly.Core.Streaming;

public sealed class Session : IAsyncDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public TcpClient Client { get; }
    public NetworkStream Stream { get; }
    public bool HasHandshaked { get; set; }
    public DateTime LastPong { get; set; } = DateTime.UtcNow;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    public Session(TcpClient client)
    {
        Client = client;
        Stream = client.GetStream();
    }

    public async Task SendAsync(MessageType type, byte[] payload, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try { await Protocol.WriteMessageAsync(Stream, type, payload, ct); }
        finally { _writeLock.Release(); }
    }

    public Task ReceiveLoopAsync(Func<MessageType, byte[], Task> handler, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var (type, payload) = await Protocol.ReadMessageAsync(Stream, ct);
                    await handler(type, payload);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { try { System.IO.File.AppendAllText(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Screenly", "engine.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Session receive loop error: {ex}{Environment.NewLine}"); } catch { } } // log in production
        }, ct);

    public async ValueTask DisposeAsync()
    {
        try { Client.Close(); } catch { }
        _writeLock.Dispose();
        await Task.CompletedTask;
    }
}

public sealed class StreamServer : IAsyncDisposable
{
    private TcpListener? _listener;
    private readonly List<Session> _sessions = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public event Action<Session>? ClientConnected;
    public event Action<Session>? ClientDisconnected;

    public int Port { get; }
    public Func<Session, MessageType, byte[], Task>? MessageHandler;

    public StreamServer(int port) => Port = port;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcp = await _listener!.AcceptTcpClientAsync(ct);
                var session = new Session(tcp);
                lock (_lock) _sessions.Add(session);
                ClientConnected?.Invoke(session);
                _ = MonitorSessionAsync(session, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore accept errors */ }
        }
    }

    private async Task MonitorSessionAsync(Session session, CancellationToken ct)
    {
        var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // Keepalive: send PING every 5s; disconnect if no PONG within 10s.
            var pingTask = Task.Run(async () =>
            {
                while (!pingCts.IsCancellationRequested)
                {
                    await Task.Delay(5000, pingCts.Token);
                    // Don't break on timeout — VDD creation blocks the receive loop.
                    // The receive loop detects disconnection via IOException.
                    try { await session.SendAsync(MessageType.Ping, Array.Empty<byte>(), pingCts.Token); }
                    catch { break; }
                }
            }, pingCts.Token);

            await session.ReceiveLoopAsync(async (type, payload) =>
            {
                if (type == MessageType.Pong) { session.LastPong = DateTime.UtcNow; return; }
                if (MessageHandler != null) await MessageHandler(session, type, payload);
            }, ct);
        }
        finally
        {
            pingCts.Cancel();
            pingCts.Dispose();
            lock (_lock) _sessions.Remove(session);
            ClientDisconnected?.Invoke(session);
            await session.DisposeAsync();
        }
    }

    public IReadOnlyList<Session> Sessions
    {
        get { lock (_lock) return _sessions.ToList(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        foreach (var s in Sessions) await s.DisposeAsync();
    }
}
