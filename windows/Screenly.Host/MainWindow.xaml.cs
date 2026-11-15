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
        InfoText.Text = "Screenly engine is running (USB mode).\n\nOn your PC run once:\n  adb reverse tcp:7543 tcp:7543\n\nThen open the Screenly app on your phone - it auto-connects over USB.\n\nPort: 7543";
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