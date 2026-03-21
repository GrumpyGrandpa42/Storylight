using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StoryLight.App.ViewModels;

namespace StoryLight.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        Closed += (_, _) =>
        {
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Left)
        {
            viewModel.HandlePreviousPageShortcut();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            viewModel.HandleNextPageShortcut();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            viewModel.HandleZoomInShortcut();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            viewModel.HandleZoomOutShortcut();
            e.Handled = true;
        }
    }
}
