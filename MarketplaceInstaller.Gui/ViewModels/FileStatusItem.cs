using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace MarketplaceInstaller.Gui.ViewModels;

public partial class FileStatusItem : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private SymbolRegular _icon = SymbolRegular.DismissCircle24;
    [ObservableProperty] private Brush _iconColor = Brushes.Gray;
}
