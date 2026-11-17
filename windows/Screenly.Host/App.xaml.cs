using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using Screenly.Core.Config;

namespace Screenly.Host;

public partial class App : System.Windows.Application
{
    private Process? _engine;
    private System.Windows.Threading.DispatcherTimer? _watchdog;
    private int _port = 7543;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        try { _port = ConfigStore.Load().Port; } catch { }
        KillStaleEngineIfNeeded();
        TryStartEngine();
        StartWatchdog();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try { _watchdog?.Stop(); } catch { }
        try { if (_engine != null && !_engine.HasExited) _engine.Kill(); } catch { }
        base.OnExit(e);
    }

    private void StartWatchdog()
    {
        _watchdog = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _watchdog.Tick += (_, __) => WatchdogTick();
        _watchdog.Start();
    }

    private void WatchdogTick()
    {
        try
        {
            if (EngineProcessAlive() && IsPortListening(_port)) return;
            KillEngine();
            TryStartEngine();
        }
        catch { }
    }

    private bool EngineProcessAlive()
    {
        if (_engine != null && !_engine.HasExited) return true;
        return Process.GetProcessesByName("Screenly.Engine").Length > 0;
    }

    private void KillStaleEngineIfNeeded()
    {
        var procs = Process.GetProcessesByName("Screenly.Engine");
        if (procs.Length == 0) return;
        if (IsPortListening(_port)) return;
        foreach (var p in procs)
        {
            try { p.Kill(); p.WaitForExit(2000); } catch { }
        }
    }

    private void KillEngine()
    {
        try { if (_engine != null && !_engine.HasExited) _engine.Kill(); } catch { }
        _engine = null;
        foreach (var p in Process.GetProcessesByName("Screenly.Engine"))
        {
            try { p.Kill(); p.WaitForExit(2000); } catch { }
        }
    }

    private bool IsPortListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect("127.0.0.1", port, null, null);
            if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500))) return false;
            client.EndConnect(result);
            return true;
        }
        catch { return false; }
    }

    private void TryStartEngine()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var enginePath = Path.Combine(dir, "Screenly.Engine.exe");
            if (!File.Exists(enginePath)) return;
            // Don't start if already running
            if (Process.GetProcessesByName("Screenly.Engine").Length > 0) return;
            _engine = new Process
            {
                StartInfo = new ProcessStartInfo(enginePath) { UseShellExecute = false, CreateNoWindow = true },
                EnableRaisingEvents = true,
            };
            _engine.Start();
        }
        catch { }
    }
}