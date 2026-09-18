using System.Windows;
using System.Windows.Input;
using FolderDiff.ViewModels;

namespace FolderDiff.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnPathDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnLeftDrop(object sender, DragEventArgs e)
    {
        if (TryGetFolder(e, out var folder) && ViewModel is { } viewModel)
            viewModel.LeftPath = folder;

        e.Handled = true;
    }

    private void OnRightDrop(object sender, DragEventArgs e)
    {
        if (TryGetFolder(e, out var folder) && ViewModel is { } viewModel)
            viewModel.RightPath = folder;

        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            DiffPane.FocusSearch();
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    private static bool TryGetFolder(DragEventArgs e, out string folder)
    {
        folder = string.Empty;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } items)
            return false;

        var candidate = items[0];
        folder = Directory.Exists(candidate)
            ? candidate
            : Path.GetDirectoryName(candidate) ?? string.Empty;

        return folder.Length > 0;
    }
}
