using System.Diagnostics;
using System.IO;

namespace Screenly.Host;

public partial class App : System.Windows.Application
{
    private Process? _engine;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        TryStartEngine();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try { if (_engine != null && !_engine.HasExited) _engine.Kill(); } catch { }
        base.OnExit(e);
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
