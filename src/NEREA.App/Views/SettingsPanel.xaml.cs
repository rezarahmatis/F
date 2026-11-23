using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using NEREA.App.ViewModels;

namespace NEREA.App.Views;

public sealed partial class SettingsPanel : UserControl
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPanel()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }
}
