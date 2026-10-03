using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Gear360.Gui.ViewModels;

namespace Gear360.Gui.Views;

/// <summary>The main window. All behaviour lives in <see cref="MainWindowViewModel"/>.</summary>
public sealed partial class MainWindow : Window
{
    private bool _closeReady;

    /// <summary>Creates the window.</summary>
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.Log.CollectionChanged += OnLogChanged;
        await vm.InitializeAsync().ConfigureAwait(true);
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && ViewModel is { Log.Count: > 0 } vm)
        {
            // Keep the newest line visible.
            Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(vm.Log.Count - 1), DispatcherPriority.Background);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeReady || ViewModel is not { } vm)
        {
            return;
        }

        // Stop any copy and release the camera before the window goes away.
        e.Cancel = true;
        vm.Log.CollectionChanged -= OnLogChanged;
        await vm.DisposeAsync().ConfigureAwait(true);
        _closeReady = true;
        Close();
    }
}
