using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NEREA.Core.Library;
using NEREA.Core.Utilities;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NEREA.App.Services;

/// <summary>Small modal prompts (rename, confirm). Dark, quiet, keyboard friendly.</summary>
public sealed class DialogService
{
    public XamlRoot? XamlRoot { get; set; }

    public async Task<string?> PromptTextAsync(string title, string placeholder, string initial = "", string primary = "OK")
    {
        if (XamlRoot == null) return null;
        var box = new TextBox { Text = initial, PlaceholderText = placeholder, MinWidth = 300, SelectionStart = 0, SelectionLength = initial.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        var text = box.Text.Trim();
        return result == ContentDialogResult.Primary && text.Length > 0 ? text : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirm = "Delete")
    {
        if (XamlRoot == null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}

/// <summary>Native Windows file / folder pickers (initialised for an unpackaged window).</summary>
public sealed class FilePickerService
{
    public Window? Window { get; set; }

    public async Task<IReadOnlyList<string>> PickMusicFilesAsync()
    {
        try
        {
            if (Window == null) return Array.Empty<string>();
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            foreach (var ext in SupportedFormats.Extensions) picker.FileTypeFilter.Add(ext);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(Window));
            var files = await picker.PickMultipleFilesAsync();
            return files?.Select(f => f.Path).ToList() ?? new List<string>();
        }
        catch (Exception ex)
        {
            Log.Warn("File picker failed.", ex);
            return Array.Empty<string>();
        }
    }

    public async Task<string?> PickFolderAsync()
    {
        try
        {
            if (Window == null) return null;
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(Window));
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            Log.Warn("Folder picker failed.", ex);
            return null;
        }
    }
}
