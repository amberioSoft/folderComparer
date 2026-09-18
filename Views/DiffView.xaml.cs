using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FolderDiff.ViewModels;

namespace FolderDiff.Views;

public partial class DiffView : UserControl
{
    private const double MinPaneWidth = 160;
    private const double MinFileListWidth = 200;
    private const double SplitterWidth = 6;

    private bool _paneSizedByUser;

    public DiffView()
    {
        InitializeComponent();
        SizeChanged += OnViewSizeChanged;
        DataContextChanged += OnDataContextChanged;
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        _paneSizedByUser = DataContext is MainViewModel { DiffSplitWidth: > 0 };

    private void OnListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (ItemsControl.ContainerFromElement((ListBox)sender, source) is ListBoxItem item)
            item.IsSelected = true;
    }

    private void OnPaneSplitterDragCompleted(object sender, DragCompletedEventArgs e) => _paneSizedByUser = true;

    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        if (e.NewValue is FileTreeNode { File: { } file })
            viewModel.SelectedFile = file;
    }

    private void OnPaneTitlesSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var total = PaneTitles.ActualWidth;
        if (total < 2 * MinPaneWidth + SplitterWidth)
            return;

        if (!_paneSizedByUser)
        {
            viewModel.DiffSplitWidth = (total - SplitterWidth) / 2;
            return;
        }

        var maximum = total - MinPaneWidth - SplitterWidth;
        if (viewModel.DiffSplitWidth > maximum)
            viewModel.DiffSplitWidth = maximum;
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var maximum = ActualWidth - 400;
        if (maximum < MinFileListWidth)
            return;

        if (viewModel.FileListWidth > maximum)
            viewModel.FileListWidth = maximum;
    }
}
