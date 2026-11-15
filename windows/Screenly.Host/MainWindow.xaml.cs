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
        InfoText.Text = "Screenly engine is running (USB mode).\n\nOn your PC run once:\n  adb reverse tcp:7543 tcp:7543\n\nThen open the Screenly app on your phone - it auto-connects over USB.\n\nPort: 7543";
    }
}
