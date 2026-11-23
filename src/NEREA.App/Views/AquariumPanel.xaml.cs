using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NEREA.App.ViewModels;

namespace NEREA.App.Views;

public sealed partial class AquariumPanel : UserControl
{
    public AquariumViewModel ViewModel { get; }

    public AquariumPanel()
    {
        ViewModel = App.Services.GetRequiredService<AquariumViewModel>();
        InitializeComponent();
    }

    private void ShowSpecies_Click(object sender, RoutedEventArgs e) => ViewModel.ShowSaved = false;

    private async void ShowSaved_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowSaved = true;
        await ViewModel.RefreshPlaylistsAsync();
    }
}
