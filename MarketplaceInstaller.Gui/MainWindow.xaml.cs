using Wpf.Ui.Controls;
using MarketplaceInstaller.Gui.ViewModels;

namespace MarketplaceInstaller.Gui;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        Loaded += async (_, _) => await vm.InitializeAsync();
    }
}
