using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Gear360.Gui.Services;

/// <summary>Posts to Avalonia's UI thread.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>Dialogs shown over a window through Avalonia's storage provider and launcher.</summary>
public sealed class AvaloniaDialogService : IDialogService
{
    private readonly Window _owner;

    /// <summary>Creates the service for dialogs owned by <paramref name="owner"/>.</summary>
    public AvaloniaDialogService(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    /// <inheritdoc />
    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        var storage = _owner.StorageProvider;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrEmpty(startFolder))
        {
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(startFolder).ConfigureAwait(true);
        }

        var folders = await storage.OpenFolderPickerAsync(options).ConfigureAwait(true);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <inheritdoc />
    public async Task<string?> PickFileAsync(string title)
    {
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = false };
        if (OperatingSystem.IsWindows())
        {
            options.FileTypeFilter = [new FilePickerFileType("Programs") { Patterns = ["*.exe"] }, FilePickerFileTypes.All];
        }

        var files = await _owner.StorageProvider.OpenFilePickerAsync(options).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var confirm = new Button { Content = confirmText, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        confirm.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { confirm, cancel },
                },
            },
        };

        await dialog.ShowDialog(_owner).ConfigureAwait(true);
        return result;
    }

    /// <inheritdoc />
    public async Task OpenFolderAsync(string path)
    {
        if (!await _owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path)).ConfigureAwait(true))
        {
            throw new IOException("The system did not open the folder.");
        }
    }
}
