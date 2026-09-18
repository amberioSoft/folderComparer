using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace FolderDiff.Services;

public sealed class DialogService : IDialogService
{
    public string? PickFolder(string title, string? initialPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            dialog.InitialDirectory = initialPath;

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public IReadOnlyList<string>? PickPngFiles(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Multiselect = true,
            Filter = "Картинки PNG (*.png)|*.png"
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }

    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Multiselect = false,
            Filter = filter
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = defaultFileName,
            Filter = "Текстовий файл (*.txt)|*.txt"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void ShowError(string message) =>
        MessageBox.Show(message, "Folder Diff", MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowInfo(string message) =>
        MessageBox.Show(message, "Folder Diff", MessageBoxButton.OK, MessageBoxImage.Information);

    public void OpenInShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError($"Не вдалося відкрити файл:{Environment.NewLine}{ex.Message}");
        }
    }

    public void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        catch (Exception ex)
        {
            ShowError($"Не вдалося відкрити папку:{Environment.NewLine}{ex.Message}");
        }
    }

    public void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            ShowError($"Не вдалося скопіювати:{Environment.NewLine}{ex.Message}");
        }
    }
}
