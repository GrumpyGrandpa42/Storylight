using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using StoryLight.App.Models;
using StoryLight.App.ViewModels;

namespace StoryLight.App.Views;

public sealed partial class MainWindow : Window
{
    private const string LibraryItemDragFormat = "application/storylight-library-item-id";
    private static readonly Thickness DefaultLibraryItemBorderThickness = new(1);
    private static readonly Thickness DropBeforeBorderThickness = new(1, 4, 1, 1);
    private static readonly Thickness DropAfterBorderThickness = new(1, 1, 1, 4);
    private static readonly IBrush DefaultLibraryItemBorderBrush = new SolidColorBrush(Color.Parse("#D5C6A8"));
    private static readonly IBrush ActiveDropBorderBrush = new SolidColorBrush(Color.Parse("#8A6844"));

    private Point? _libraryDragStartPoint;
    private LibraryItem? _libraryDragSourceItem;
    private bool _isLibraryDragInProgress;
    private Border? _libraryDropTarget;
    private bool _libraryDropInsertAfter;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnLibraryItemDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnLibraryItemDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnLibraryItemDrop, RoutingStrategies.Bubble, handledEventsToo: true);
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
        if (sender is not Border control
            || DataContext is not MainWindowViewModel viewModel
            || control.DataContext is not LibraryItem item)
        {
            return;
        }

        if (!e.GetCurrentPoint(control).Properties.IsRightButtonPressed)
        {
            viewModel.SelectLibraryItem(item);

            if (e.GetCurrentPoint(control).Properties.IsLeftButtonPressed
                && !IsPointerFromButton(e.Source))
            {
                _libraryDragStartPoint = e.GetPosition(control);
                _libraryDragSourceItem = item;
            }
            else
            {
                ClearPendingLibraryDrag();
            }

            return;
        }

        ClearPendingLibraryDrag();
        OpenLibraryItemContextMenu(control, item, viewModel);
        e.Handled = true;
    }

    private async void OnLibraryItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border control
            || _isLibraryDragInProgress
            || _libraryDragStartPoint is null
            || _libraryDragSourceItem is null
            || control.DataContext is not LibraryItem item
            || item != _libraryDragSourceItem)
        {
            return;
        }

        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            ClearPendingLibraryDrag();
            return;
        }

        var delta = e.GetPosition(control) - _libraryDragStartPoint.Value;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return;
        }

        _isLibraryDragInProgress = true;
        try
        {
            var data = new DataObject();
            data.Set(LibraryItemDragFormat, _libraryDragSourceItem.Id);
            data.Set(DataFormats.Text, _libraryDragSourceItem.Id);

            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            e.Handled = true;
        }
        finally
        {
            _isLibraryDragInProgress = false;
            ClearPendingLibraryDrag();
            ClearLibraryItemDropHint();
        }
    }

    private void OnLibraryItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isLibraryDragInProgress)
        {
            ClearPendingLibraryDrag();
        }
    }

    private void OnLibraryItemMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control
            || DataContext is not MainWindowViewModel viewModel
            || control.DataContext is not LibraryItem item)
        {
            return;
        }

        OpenLibraryItemContextMenu(control, item, viewModel);
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

    private static void OpenLibraryItemContextMenu(Control anchor, LibraryItem item, MainWindowViewModel viewModel)
    {
        viewModel.SelectLibraryItem(item);

        var contextMenu = BuildLibraryItemContextMenu(viewModel);
        anchor.ContextMenu = contextMenu;
        contextMenu.Open(anchor);
    }

    private void OnLibraryItemDragOver(object? sender, DragEventArgs e)
    {
        var control = FindLibraryItemBorder(e.Source);
        if (control is null
            || DataContext is not MainWindowViewModel viewModel
            || control.DataContext is not LibraryItem targetItem
            || viewModel.IsBusy
            || !TryGetDraggedLibraryItem(e, viewModel, out var sourceItem)
            || sourceItem == targetItem)
        {
            e.DragEffects = DragDropEffects.None;
            ClearLibraryItemDropHint();
            e.Handled = true;
            return;
        }

        var insertAfter = ShouldInsertAfter(control, e);
        ApplyLibraryItemDropHint(control, insertAfter);
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnLibraryItemDragLeave(object? sender, RoutedEventArgs e)
    {
        if (FindLibraryItemBorder(e.Source) == _libraryDropTarget)
        {
            ClearLibraryItemDropHint();
        }
    }

    private async void OnLibraryItemDrop(object? sender, DragEventArgs e)
    {
        try
        {
            var control = FindLibraryItemBorder(e.Source);
            if (control is null
                || DataContext is not MainWindowViewModel viewModel
                || control.DataContext is not LibraryItem targetItem
                || !TryGetDraggedLibraryItem(e, viewModel, out var sourceItem)
                || sourceItem == targetItem)
            {
                return;
            }

            await viewModel.MoveLibraryItemAsync(sourceItem, targetItem, ShouldInsertAfter(control, e));
            e.Handled = true;
        }
        finally
        {
            ClearLibraryItemDropHint();
        }
    }

    private void OnChapterHeadingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control
            || DataContext is not MainWindowViewModel viewModel
            || viewModel.ChapterItems.Count == 0)
        {
            return;
        }

        var contextMenu = BuildChapterContextMenu(viewModel);
        control.ContextMenu = contextMenu;
        contextMenu.Open(control);
        e.Handled = true;
    }

    private static ContextMenu BuildChapterContextMenu(MainWindowViewModel viewModel)
    {
        var contextMenu = new ContextMenu();

        foreach (var chapter in viewModel.ChapterItems)
        {
            contextMenu.Items.Add(new MenuItem
            {
                Header = chapter.IsCurrent ? $"• {chapter.Title}" : chapter.Title,
                Command = chapter.ActivateCommand
            });
        }

        return contextMenu;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private static bool IsPointerFromButton(object? source)
    {
        return source is Visual visual
            && (visual is Button || visual.FindAncestorOfType<Button>() is not null);
    }

    private static Border? FindLibraryItemBorder(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Border border && border.DataContext is LibraryItem)
            {
                return border;
            }
        }

        return null;
    }

    private static bool ShouldInsertAfter(Border control, DragEventArgs e)
    {
        var position = e.GetPosition(control);
        return position.Y >= control.Bounds.Height / 2;
    }

    private static bool TryGetDraggedLibraryItem(DragEventArgs e, MainWindowViewModel viewModel, out LibraryItem sourceItem)
    {
        sourceItem = null!;
        if (!e.Data.Contains(LibraryItemDragFormat)
            || e.Data.Get(LibraryItemDragFormat) is not string libraryItemId)
        {
            return false;
        }

        var libraryItem = viewModel.LibraryItems.FirstOrDefault(item => item.Id == libraryItemId);
        if (libraryItem is null)
        {
            return false;
        }

        sourceItem = libraryItem;
        return true;
    }

    private void ApplyLibraryItemDropHint(Border control, bool insertAfter)
    {
        if (_libraryDropTarget == control && _libraryDropInsertAfter == insertAfter)
        {
            return;
        }

        ClearLibraryItemDropHint();
        _libraryDropTarget = control;
        _libraryDropInsertAfter = insertAfter;
        control.BorderBrush = ActiveDropBorderBrush;
        control.BorderThickness = insertAfter ? DropAfterBorderThickness : DropBeforeBorderThickness;
    }

    private void ClearLibraryItemDropHint()
    {
        if (_libraryDropTarget is null)
        {
            return;
        }

        _libraryDropTarget.BorderBrush = DefaultLibraryItemBorderBrush;
        _libraryDropTarget.BorderThickness = DefaultLibraryItemBorderThickness;
        _libraryDropTarget = null;
        _libraryDropInsertAfter = false;
    }

    private void ClearPendingLibraryDrag()
    {
        _libraryDragStartPoint = null;
        _libraryDragSourceItem = null;
    }
}
