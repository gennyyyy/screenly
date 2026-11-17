using System.Drawing;
using System.Windows;

namespace Screenly.Host;

public partial class MainWindow : System.Windows.Window
{
    private readonly System.Windows.Forms.NotifyIcon _tray;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowInfo();

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Screenly",
            Visible = true,
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Exit", null, (_, _) => { _tray.Visible = false; System.Windows.Application.Current.Shutdown(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();

        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(1000, "Screenly", "Running in the system tray.", System.Windows.Forms.ToolTipIcon.Info);
        };
    }

    private void ShowInfo()
    {
        // Wait briefly for engine to start listening
        for (int i = 0; i < 20; i++)
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                c.Connect("127.0.0.1", 7543);
                c.Close();
                string statusLine = "";
                var statusPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Screenly", "status.json");
                for (int j = 0; j < 5; j++)
                {
                    if (System.IO.File.Exists(statusPath))
                    {
                        try
                        {
                            var json = System.IO.File.ReadAllText(statusPath);
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            var root = doc.RootElement;
                            var codec = root.TryGetProperty("codec", out var cEl) ? (cEl.GetString() ?? "?") : "?";
                            var w = root.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 0;
                            var h = root.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 0;
                            var fps = root.TryGetProperty("fps", out var fEl) ? fEl.GetInt32() : 0;
                            var connType = root.TryGetProperty("connectionType", out var ctEl) ? (ctEl.GetString() ?? "?") : "?";
                            var devices = root.TryGetProperty("connectedDevices", out var cdEl) ? cdEl.GetInt32() : 0;
                            statusLine = "\n\nEncoder: " + codec + " " + w + "x" + h + " @ " + fps + " fps" +
                                "\nConnection: " + connType + "  Devices: " + devices;
                        }
                        catch { }
                        break;
                    }
                    System.Threading.Thread.Sleep(500);
                }
                InfoText.Text = "Screenly engine is running (USB mode).\n\nOn your PC run once:\n  adb reverse tcp:7543 tcp:7543\n\nThen open the Screenly app on your phone - it auto-connects over USB.\n\nPort: 8080" + statusLine;
                return;
            }
            catch { System.Threading.Thread.Sleep(500); }
        }
        InfoText.Text = "Engine may not have started. Check that Screenly.Engine.exe is running.\nPort: 8080";
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray.Visible = false;
        _tray.Dispose();
        base.OnClosed(e);
    }
}