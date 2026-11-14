using System.Net;
using System.Net.Sockets;

namespace Screenly.Host;

public partial class MainWindow : System.Windows.Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowInfo();
    }

    private void ShowInfo()
    {
        try
        {
            var ip = GetLocalIp();
            InfoText.Text = $"Screenly engine is running.\n\nConnect your Android phone to:\n  Host: {ip}\n  Port: 7543\n\nOpen the Screenly app on your phone and pick this host (or enter the IP manually).";
        }
        catch (Exception ex)
        {
            InfoText.Text = $"Started. (Could not detect IP: {ex.Message})\nPort: 7543";
        }
    }

    private static string GetLocalIp()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Connect("8.8.8.8", 65530);
        return (s.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
    }
}
