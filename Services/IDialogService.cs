namespace FolderDiff.Services;

public interface IDialogService
{
    string? PickFolder(string title, string? initialPath);

    IReadOnlyList<string>? PickPngFiles(string title);

    string? PickFile(string title, string filter);

    string? PickSaveFile(string title, string defaultFileName);

    void ShowError(string message);

    void ShowInfo(string message);

    void OpenInShell(string path);

    void RevealInExplorer(string path);

    void CopyToClipboard(string text);
}
