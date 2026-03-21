using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StoryLight.App.Models;
using StoryLight.App.ViewModels;

namespace StoryLight.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        Activated += (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.HandleWindowActivated();
            }
        };
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

    private void OnLibraryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.OpenSelectedCommand.CanExecute(null))
        {
            viewModel.OpenSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnLibraryItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control
            || DataContext is not MainWindowViewModel viewModel
            || control.DataContext is not LibraryItem item)
        {
            return;
        }

        viewModel.SelectLibraryItem(item);

        if (!e.GetCurrentPoint(control).Properties.IsRightButtonPressed)
        {
            return;
        }

        var contextMenu = BuildLibraryItemContextMenu(viewModel);
        control.ContextMenu = contextMenu;
        contextMenu.Open(control);
        e.Handled = true;
    }

    private static ContextMenu BuildLibraryItemContextMenu(MainWindowViewModel viewModel)
    {
        return new ContextMenu
        {
            Items =
            {
                new MenuItem
                {
                    Header = "Open",
                    Command = viewModel.OpenSelectedCommand
                },
                new MenuItem
                {
                    Header = "Remove",
                    Command = viewModel.RemoveSelectedCommand
                },
                new MenuItem
                {
                    Header = "Delete EPUB",
                    Command = viewModel.DeleteSavedEpubCommand
                }
            }
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
