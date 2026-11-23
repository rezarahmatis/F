using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NEREA.App.ViewModels;
using Windows.System;

namespace NEREA.App.Views;

public sealed partial class LibraryPanel : UserControl
{
    public LibraryViewModel ViewModel { get; }

    public LibraryPanel()
    {
        ViewModel = App.Services.GetRequiredService<LibraryViewModel>();
        InitializeComponent();
    }

    public void FocusSearch()
    {
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private static LibraryRow? RowOf(object? source) => (source as FrameworkElement)?.DataContext as LibraryRow;

    private void RowList_ItemClick(object sender, ItemClickEventArgs e)
    {
        // Single click opens albums, artists, folders and playlists; songs need a double click (or Enter).
        if (e.ClickedItem is LibraryRow row && row.Kind != LibraryRowKind.Track)
            ViewModel.Activate(row);
    }

    private void RowList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var row = RowOf(e.OriginalSource);
        if (row is { Kind: LibraryRowKind.Track }) ViewModel.Activate(row);
    }

    private void RowList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && RowList.SelectedItem is LibraryRow row)
        {
            ViewModel.Activate(row);
            e.Handled = true;
        }
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) ViewModel.ToggleFavorite(row);
    }

    private void RowList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var row = RowOf(e.OriginalSource);
        if (row == null) return;
        RowList.SelectedItem = row;

        var menu = new MenuFlyout();
        void Add(string text, Action action, string? glyph = null)
        {
            var item = new MenuFlyoutItem { Text = text };
            if (glyph != null) item.Icon = new FontIcon { Glyph = glyph };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (row.Kind == LibraryRowKind.Playlist)
        {
            Add("Play", () => ViewModel.PlayRow(row), "\uE768");
            Add("Add to queue", () => ViewModel.EnqueueRow(row), "\uE710");
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Rename…", async () => await ViewModel.RenamePlaylistAsync(row), "\uE70F");
            Add("Delete", async () => await ViewModel.DeletePlaylistAsync(row), "\uE74D");
        }
        else
        {
            Add("Play", () => ViewModel.PlayRow(row), "\uE768");
            Add("Play next", () => ViewModel.PlayNextRow(row), "\uE893");
            Add("Add to queue", () => ViewModel.EnqueueRow(row), "\uE710");
            if (row.Track != null)
                Add(row.IsFavorite ? "Remove from favorites" : "Add to favorites", () => ViewModel.ToggleFavorite(row), "\uE734");

            var sub = new MenuFlyoutSubItem { Text = "Add to playlist", Icon = new FontIcon { Glyph = "\uE8F1" } };
            foreach (var p in ViewModel.Playlists)
            {
                var id = p.Id;
                var pi = new MenuFlyoutItem { Text = p.Name };
                pi.Click += (_, _) => ViewModel.AddToPlaylist(row, id);
                sub.Items.Add(pi);
            }
            if (ViewModel.Playlists.Count > 0) sub.Items.Add(new MenuFlyoutSeparator());
            var create = new MenuFlyoutItem { Text = "New playlist…" };
            create.Click += async (_, _) => await ViewModel.NewPlaylistWithAsync(row);
            sub.Items.Add(create);
            menu.Items.Add(sub);

            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Show in Explorer", () => ViewModel.ShowInExplorer(row), "\uE838");
            if (ViewModel.IsPlaylistsTab == false && ViewModel.CanGoBack && row.Track != null && ViewModel.TabIndex == (int)LibraryTab.Playlists)
                Add("Remove from playlist", () => ViewModel.RemoveFromPlaylist(row), "\uE738");
            Add("Remove from library", () => ViewModel.RemoveFromLibrary(row), "\uE74D");
        }

        menu.ShowAt(RowList, e.GetPosition(RowList));
        e.Handled = true;
    }
}
