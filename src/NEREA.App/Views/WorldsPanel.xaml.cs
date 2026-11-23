using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using NEREA.App.ViewModels;

namespace NEREA.App.Views;

public sealed partial class WorldsPanel : UserControl
{
    public WorldsViewModel ViewModel { get; }

    public WorldsPanel()
    {
        ViewModel = App.Services.GetRequiredService<WorldsViewModel>();
        InitializeComponent();
    }

    private void WorldList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WorldRow row) ViewModel.Select(row);
    }
}
