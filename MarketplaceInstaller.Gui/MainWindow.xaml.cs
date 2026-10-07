using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Controls;
using MarketplaceInstaller.Gui.ViewModels;

namespace MarketplaceInstaller.Gui;

public partial class MainWindow : FluentWindow
{
    /// <summary>Window corner radius, matching the root Border in MainWindow.xaml.</summary>
    private const double WindowCornerRadius = 20;

    // DWMWA_WINDOW_CORNER_PREFERENCE (33) / DWMWCP_ROUND (2) — Windows 11 only.
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
        ref int value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right,
        int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        Loaded += async (_, _) => await vm.InitializeAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RoundCorners();
        SizeChanged += (_, _) => RoundCorners();
        StateChanged += (_, _) => RoundCorners();
    }

    /// <summary>
    /// Windows 11 rounds top-level windows through DWM; Windows 10's DWM cannot round at
    /// all, so there the window is clipped to a rounded region instead. Both keep normal
    /// dragging and resizing. A maximized window is left square so no gaps show along the
    /// screen edges, and the system owns each region handle once it is set.
    /// </summary>
    private void RoundCorners()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (Environment.OSVersion.Version.Build >= 22000)
        {
            int preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }

        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int width = (int)Math.Ceiling(ActualWidth * scale);
        int height = (int)Math.Ceiling(ActualHeight * scale);
        if (width <= 0 || height <= 0) return;

        int radius = (int)Math.Round(WindowCornerRadius * scale);
        SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, width, height, radius * 2, radius * 2), true);
    }
}
