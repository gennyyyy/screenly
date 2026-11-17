using System.Drawing;
using System.Windows;
using System.Windows.Media;

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
        for (int i = 0; i < 20; i++)
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                c.Connect("127.0.0.1", 7543);
                c.Close();

                string codec = "—", connType = "—";
                int w = 0, h = 0, fps = 0, devices = 0;
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
                            codec = root.TryGetProperty("codec", out var cEl) ? (cEl.GetString() ?? "—") : "—";
                            w = root.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 0;
                            h = root.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 0;
                            fps = root.TryGetProperty("fps", out var fEl) ? fEl.GetInt32() : 0;
                            connType = root.TryGetProperty("connectionType", out var ctEl) ? (ctEl.GetString() ?? "—") : "—";
                            devices = root.TryGetProperty("connectedDevices", out var cdEl) ? cdEl.GetInt32() : 0;
                        }
                        catch { }
                        break;
                    }
                    System.Threading.Thread.Sleep(500);
                }

                StatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xD3, 0x99));
                StatusText.Text = "Engine running";
                EncoderText.Text = codec.ToUpperInvariant();
                ResolutionText.Text = $"{w} × {h}  @ {fps} fps";
                ConnectionText.Text = connType;
                DevicesText.Text = devices.ToString();
                return;
            }
            catch { System.Threading.Thread.Sleep(500); }
        }
        StatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71));
        StatusText.Text = "Engine not running";
        EncoderText.Text = "—";
        ResolutionText.Text = "—";
        ConnectionText.Text = "—";
        DevicesText.Text = "—";
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