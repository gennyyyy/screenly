using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Screenly.Host;

public partial class MainWindow : System.Windows.Window
{
    private readonly System.Windows.Forms.NotifyIcon _tray;

    // True until ShowInfo has reflected the saved config into the selectors. Setting IsSelected
    // raises SelectionChanged, and the app must never rewrite config.json just by starting.
    private bool _suppressSelectionChange = true;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowInfo();

        // Poll status.json every second for live FPS/codec updates
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshStatus();
        timer.Start();

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
        // Reflect current encoder and fps in the selectors under the guard: this must not save.
        try
        {
            _suppressSelectionChange = true;
            var cfg = Screenly.Core.Config.ConfigStore.Load();
            var codec = (cfg.Encoding.Codec ?? "jpeg").ToLowerInvariant();
            foreach (ComboBoxItem item in CodecCombo.Items)
                item.IsSelected = (string)item.Tag == codec;
            foreach (ComboBoxItem item in FpsCombo.Items)
                item.IsSelected = (string)item.Tag == cfg.Encoding.TargetFps.ToString();
        }
        catch { }
        finally { _suppressSelectionChange = false; }

        for (int i = 0; i < 20; i++)
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                c.Connect("127.0.0.1", 7543);
                c.Close();
                StatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xD3, 0x99));
                StatusText.Text = "Engine running";
                RefreshStatus();
                return;
            }
            catch { System.Threading.Thread.Sleep(500); }
        }
        StatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71));
        StatusText.Text = "Engine not running";
    }

    private void CodecCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Initialization/reflection only: never save from here.
        if (_suppressSelectionChange) return;
        if (CodecCombo.SelectedItem is not ComboBoxItem item) return;
        var codec = (string)item.Tag;
        try
        {
            var cfg = Screenly.Core.Config.ConfigStore.Load();
            if ((cfg.Encoding.Codec ?? "").ToLowerInvariant() == codec) return;
            cfg = cfg with { Encoding = cfg.Encoding with { Codec = codec } };
            Screenly.Core.Config.ConfigStore.Save(cfg);
            EncoderText.Text = codec.ToUpperInvariant();
        }
        catch { }
    }

    private void FpsCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Initialization/reflection only: never save from here.
        if (_suppressSelectionChange) return;
        if (FpsCombo.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse((string)item.Tag, out var fps)) return;
        try
        {
            var cfg = Screenly.Core.Config.ConfigStore.Load();
            if (cfg.Encoding.TargetFps == fps) return;
            cfg = cfg with { Encoding = cfg.Encoding with { TargetFps = fps } };
            Screenly.Core.Config.ConfigStore.Save(cfg);
        }
        catch { }
    }

    private void RefreshStatus()
    {
        try
        {
            var statusPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Screenly", "status.json");
            if (!System.IO.File.Exists(statusPath)) return;
            var json = System.IO.File.ReadAllText(statusPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var codec = root.TryGetProperty("codec", out var cEl) ? (cEl.GetString() ?? "—") : "—";
            var w = root.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 0;
            var h = root.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 0;
            var fps = root.TryGetProperty("fps", out var fEl) ? fEl.GetInt32() : 0;
            var connType = root.TryGetProperty("connectionType", out var ctEl) ? (ctEl.GetString() ?? "—") : "—";
            var devices = root.TryGetProperty("connectedDevices", out var cdEl) ? cdEl.GetInt32() : 0;

            EncoderText.Text = codec.ToUpperInvariant();
            ResolutionText.Text = $"{w} × {h}  @ {fps} fps";
            ConnectionText.Text = connType;
            DevicesText.Text = devices.ToString();

            // H.264 diagnostics: specific error, or live stats when healthy.
            var encoderError = root.TryGetProperty("encoderError", out var eeEl) && eeEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? eeEl.GetString()
                : null;
            if (!string.IsNullOrEmpty(encoderError))
            {
                EncoderErrorText.Text = "Encoder error: " + encoderError.Split('\n')[0].Trim();
                EncoderErrorText.ToolTip = encoderError;
                EncoderErrorText.Visibility = Visibility.Visible;
            }
            else
            {
                EncoderErrorText.Text = "";
                EncoderErrorText.Visibility = Visibility.Collapsed;
            }

            var frames = root.TryGetProperty("framesEncoded", out var feEl) ? feEl.GetInt64() : 0;
            var bytesOut = root.TryGetProperty("bytesOut", out var boEl) ? boEl.GetInt64() : 0;
            var msAvg = root.TryGetProperty("encodeMsAvg", out var eaEl) ? eaEl.GetDouble() : 0;
            if (codec == "h264" && frames > 0)
            {
                H264StatsText.Text = $"{frames} frames · {bytesOut / 1048576.0:F1} MB · {msAvg:F1} ms avg encode";
                H264StatsText.Visibility = Visibility.Visible;
            }
            else
            {
                H264StatsText.Text = "";
                H264StatsText.Visibility = Visibility.Collapsed;
            }
        }
        catch { }
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