using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MarketplaceInstaller.Gui.ViewModels;

public partial class StepItem : ObservableObject
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private Brush _bar = Brushes.Transparent;
}
