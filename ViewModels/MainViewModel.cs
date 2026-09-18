using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderDiff.Models;
using FolderDiff.Services;

namespace FolderDiff.ViewModels;

public partial class MainViewModel : ObservableObject, IComparePaths, IDisposable
{
    private const long MaxDiffBytes = 20L * 1024 * 1024;

    private readonly FolderComparer _comparer = new();
    private readonly DiffService _diffService = new();
    private readonly FolderWatcher _watcher = new();
    private readonly IDialogService _dialogs;
    private readonly SettingsService _settingsService;

    private List<FileItemViewModel> _allFiles = new();
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<int> _changeStarts = Array.Empty<int>();
    private int _currentChange = -1;
    private int _diffVersion;
    private string _selectedLeftText = string.Empty;
    private string _selectedRightText = string.Empty;

    public MainViewModel(IDialogService dialogs, SettingsService settingsService, ImageTransferService transfer)
    {
        _dialogs = dialogs;
        _settingsService = settingsService;
        StatusFilters = StatusFilterOption.CreateDefaults();
        _selectedStatusFilter = StatusFilters[0];
        WhitespaceOptions = WhitespaceOption.CreateDefaults();
        _selectedWhitespace = WhitespaceOptions[0];

        var settings = settingsService.Load();
        _leftPath = settings.LeftPath;
        _rightPath = settings.RightPath;
        _ignorePatterns = settings.IgnorePatterns;
        _selectedWhitespace = WhitespaceOptions.FirstOrDefault(o => (int)o.Mode == settings.WhitespaceMode)
                              ?? WhitespaceOptions[0];
        _groupByFolder = settings.GroupByFolder;
        _ignoreLineEndings = settings.IgnoreLineEndings;
        _isSideBySide = settings.IsSideBySide;
        _collapseUnchanged = settings.CollapseUnchanged;
        _contextLines = ContextLineOptions.Contains(settings.ContextLines) ? settings.ContextLines : 3;
        _watchChanges = settings.WatchChanges;
        _diffSplitWidth = settings.DiffSplitWidth;
        _fileListWidth = settings.FileListWidth > 0 ? settings.FileListWidth : 380;

        _watcher.Changed += OnWatchedFolderChanged;

        Transfer = new TransferViewModel(transfer, dialogs, this);
        Keys = new KeysViewModel(transfer, dialogs);
        Transfer.ApplySettings(settings);
        Keys.ApplySettings(settings);
    }

    public TransferViewModel Transfer { get; private set; } = null!;

    public KeysViewModel Keys { get; private set; } = null!;

    public IReadOnlyList<string> ExcludePatterns
    {
        get
        {
            var result = new List<string>();

            foreach (var pattern in ParsePatterns(IgnorePatterns))
            {
                result.Add(pattern);
                if (!pattern.Contains('/') && !pattern.Contains('\\') &&
                    !pattern.Contains('*') && !pattern.Contains('?'))
                    result.Add(pattern + "/");
            }

            return result;
        }
    }

    public void PersistSettings()
    {
        var settings = new AppSettings
        {
            LeftPath = LeftPath,
            RightPath = RightPath,
            IgnorePatterns = IgnorePatterns,
            WhitespaceMode = (int)Whitespace,
            GroupByFolder = GroupByFolder,
            IgnoreLineEndings = IgnoreLineEndings,
            IsSideBySide = IsSideBySide,
            CollapseUnchanged = CollapseUnchanged,
            ContextLines = ContextLines,
            WatchChanges = WatchChanges,
            DiffSplitWidth = DiffSplitWidth,
            FileListWidth = FileListWidth
        };

        Transfer.WriteSettings(settings);
        Keys.WriteSettings(settings);
        _settingsService.Save(settings);
    }

    public void Dispose()
    {
        _watcher.Changed -= OnWatchedFolderChanged;
        _watcher.Dispose();
        _cancellation?.Dispose();
    }

    public IReadOnlyList<StatusFilterOption> StatusFilters { get; }

    public IReadOnlyList<WhitespaceOption> WhitespaceOptions { get; }

    public IReadOnlyList<int> ContextLineOptions { get; } = new[] { 1, 3, 5, 10, 25 };

    public WhitespaceMode Whitespace => SelectedWhitespace.Mode;

    public string WhitespaceHint => SelectedWhitespace.Hint;

    public ObservableCollection<FileTreeNode> FileTree { get; private set; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private string _leftPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private string _rightPath = string.Empty;

    [ObservableProperty]
    private string _ignorePatterns = ".git;.vs;bin;obj;node_modules;packages;dist;*.user;*.suo";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhitespaceHint))]
    private WhitespaceOption _selectedWhitespace;

    [ObservableProperty]
    private bool _groupByFolder = true;

    [ObservableProperty]
    private bool _ignoreLineEndings = true;

    [ObservableProperty]
    private bool _isSideBySide = true;

    [ObservableProperty]
    private bool _collapseUnchanged;

    [ObservableProperty]
    private int _contextLines = 3;

    [ObservableProperty]
    private double _diffSplitWidth;

    [ObservableProperty]
    private double _fileListWidth = 380;

    [ObservableProperty]
    private bool _watchChanges;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private StatusFilterOption _selectedStatusFilter;

    [ObservableProperty]
    private IReadOnlyList<FileItemViewModel> _visibleFiles = Array.Empty<FileItemViewModel>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevealSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyPathCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyDiffCommand))]
    private FileItemViewModel? _selectedFile;

    [ObservableProperty]
    private IReadOnlyList<DiffRow> _diffRows = Array.Empty<DiffRow>();

    [ObservableProperty]
    private int _scrollToRow = -1;

    [ObservableProperty]
    private string _diffHeader = "Виберіть файл зі списку";

    [ObservableProperty]
    private string _diffStats = string.Empty;

    [ObservableProperty]
    private string _leftPaneTitle = "До";

    [ObservableProperty]
    private string _rightPaneTitle = "Після";

    [ObservableProperty]
    private string _statusText = "Вкажіть дві папки та натисніть «Порівняти»";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _hasResult;

    [ObservableProperty]
    private int _addedCount;

    [ObservableProperty]
    private int _deletedCount;

    [ObservableProperty]
    private int _modifiedCount;

    [ObservableProperty]
    private int _unchangedCount;

    private bool CanCompare() =>
        !IsBusy && !string.IsNullOrWhiteSpace(LeftPath) && !string.IsNullOrWhiteSpace(RightPath);

    private bool CanRefresh() => CanCompare() && HasResult;

    private bool CanCancel() => IsBusy;

    private bool HasSelection() => SelectedFile is not null;

    [RelayCommand]
    private void BrowseLeft()
    {
        var picked = _dialogs.PickFolder("Ліва папка (версія «до»)", LeftPath);
        if (picked is not null)
            LeftPath = picked;
    }

    [RelayCommand]
    private void BrowseRight()
    {
        var picked = _dialogs.PickFolder("Права папка (версія «після»)", RightPath);
        if (picked is not null)
            RightPath = picked;
    }

    [RelayCommand]
    private void Swap() => (LeftPath, RightPath) = (RightPath, LeftPath);

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private Task Compare() => RunCompareAsync(preserveSelection: false);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task Refresh() => RunCompareAsync(preserveSelection: true);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenSelected()
    {
        var path = SelectedFile?.Entry.RightFullPath ?? SelectedFile?.Entry.LeftFullPath;
        if (path is not null)
            _dialogs.OpenInShell(path);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RevealSelected()
    {
        var path = SelectedFile?.Entry.RightFullPath ?? SelectedFile?.Entry.LeftFullPath;
        if (path is not null)
            _dialogs.RevealInExplorer(path);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPath()
    {
        var path = SelectedFile?.Entry.RightFullPath ?? SelectedFile?.Entry.LeftFullPath;
        if (path is not null)
            _dialogs.CopyToClipboard(path);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyDiff()
    {
        if (SelectedFile?.Entry.IsBinary != false)
            return;

        var text = _diffService.BuildUnifiedText(_selectedLeftText, _selectedRightText, Whitespace);
        _dialogs.CopyToClipboard(text);
    }

    [RelayCommand]
    private void CopyRow(DiffRow? row)
    {
        if (row is null)
            return;

        var text = row.IsSeparator
            ? row.SeparatorText
            : IsSideBySide
                ? $"{row.LeftText}{Environment.NewLine}{row.RightText}"
                : row.LeftText;

        _dialogs.CopyToClipboard(text);
    }

    [RelayCommand]
    private void NextChange() => MoveToChange(1);

    [RelayCommand]
    private void PreviousChange() => MoveToChange(-1);

    private void MoveToChange(int direction)
    {
        if (_changeStarts.Count == 0)
            return;

        _currentChange = _currentChange < 0
            ? (direction > 0 ? 0 : _changeStarts.Count - 1)
            : Math.Clamp(_currentChange + direction, 0, _changeStarts.Count - 1);

        ScrollToRow = -1;
        ScrollToRow = _changeStarts[_currentChange];
        StatusText = $"Зміна {_currentChange + 1} з {_changeStarts.Count}";
    }

    private async Task RunCompareAsync(bool preserveSelection)
    {
        if (!Directory.Exists(LeftPath))
        {
            _dialogs.ShowError($"Ліва папка не знайдена:{Environment.NewLine}{LeftPath}");
            return;
        }

        if (!Directory.Exists(RightPath))
        {
            _dialogs.ShowError($"Права папка не знайдена:{Environment.NewLine}{RightPath}");
            return;
        }

        var previousPath = preserveSelection ? SelectedFile?.RelativePath : null;

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        StatusText = "Сканування...";
        SelectedFile = null;
        DiffRows = Array.Empty<DiffRow>();
        _allFiles = new List<FileItemViewModel>();
        VisibleFiles = Array.Empty<FileItemViewModel>();

        var options = new CompareOptions
        {
            IgnorePatterns = ParsePatterns(IgnorePatterns),
            Whitespace = Whitespace,
            IgnoreLineEndings = IgnoreLineEndings
        };

        var progress = new Progress<CompareProgress>(p =>
        {
            StatusText = p.Message;
            if (p.Total > 0)
            {
                IsProgressIndeterminate = false;
                ProgressValue = 100.0 * p.Done / p.Total;
            }
        });

        try
        {
            var result = await _comparer.CompareAsync(LeftPath, RightPath, options, progress, token);

            _allFiles = result.Entries.Select(e => new FileItemViewModel(e)).ToList();
            AddedCount = result.AddedCount;
            DeletedCount = result.DeletedCount;
            ModifiedCount = result.ModifiedCount;
            UnchangedCount = result.UnchangedCount;
            LeftPaneTitle = $"До  ·  {result.LeftRoot}";
            RightPaneTitle = $"Після  ·  {result.RightRoot}";
            HasResult = true;
            ApplyFilter();

            SyncTreeSelection();
            SelectedFile = previousPath is null
                ? FirstInteresting()
                : VisibleFiles.FirstOrDefault(f =>
                      string.Equals(f.RelativePath, previousPath, StringComparison.OrdinalIgnoreCase))
                  ?? FirstInteresting();

            var changed = AddedCount + DeletedCount + ModifiedCount;
            StatusText = changed == 0
                ? $"Різниць немає. Файлів: {result.Entries.Count}. {result.Elapsed.TotalSeconds:0.00} с"
                : $"Змін: {changed} з {result.Entries.Count} файлів. {result.Elapsed.TotalSeconds:0.00} с";

            if (result.Errors.Count > 0)
                StatusText += $" | пропущено з помилками: {result.Errors.Count}";

            UpdateWatcher();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Скасовано";
        }
        catch (Exception ex)
        {
            StatusText = "Помилка порівняння";
            _dialogs.ShowError(ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
        }
    }

    partial void OnWatchChangesChanged(bool value) => UpdateWatcher();

    private void UpdateWatcher()
    {
        if (WatchChanges && HasResult)
            _watcher.Start(new[] { LeftPath, RightPath });
        else
            _watcher.Stop();
    }

    private void OnWatchedFolderChanged(object? sender, EventArgs e)
    {
        if (!WatchChanges || IsBusy || !HasResult)
            return;

        StatusText = "Виявлено зміни у папках, оновлюю...";
        _ = RunCompareAsync(preserveSelection: true);
    }

    partial void OnIgnorePatternsChanged(string value) => OnPropertyChanged(nameof(ExcludePatterns));

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedStatusFilterChanged(StatusFilterOption value) => ApplyFilter();

    partial void OnDiffRowsChanged(IReadOnlyList<DiffRow> value)
    {
        if (_changeStarts.Count == 0)
        {
            _currentChange = -1;
            ScrollToRow = 0;
            return;
        }

        _currentChange = 0;
        ScrollToRow = _changeStarts[0];
    }

    partial void OnIsSideBySideChanged(bool value) => _ = LoadDiffAsync(SelectedFile);

    partial void OnCollapseUnchangedChanged(bool value) => _ = LoadDiffAsync(SelectedFile);

    partial void OnContextLinesChanged(int value) => _ = LoadDiffAsync(SelectedFile);

    partial void OnSelectedWhitespaceChanged(WhitespaceOption value) => RecompareOrReload();

    partial void OnGroupByFolderChanged(bool value) => ApplyFilter();

    partial void OnIgnoreLineEndingsChanged(bool value) => RecompareOrReload();

    private void RecompareOrReload()
    {
        if (HasResult && !IsBusy)
            _ = RunCompareAsync(preserveSelection: true);
        else
            _ = LoadDiffAsync(SelectedFile);
    }

    partial void OnSelectedFileChanged(FileItemViewModel? value) => _ = LoadDiffAsync(value);

    private FileItemViewModel? FirstInteresting() =>
        VisibleFiles.FirstOrDefault(f => !f.Entry.IsBinary && f.Status == FileStatus.Modified)
        ?? VisibleFiles.FirstOrDefault(f => !f.Entry.IsBinary)
        ?? VisibleFiles.FirstOrDefault();

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        var filter = SelectedStatusFilter;

        var filtered = _allFiles
            .Where(f => filter.Match(f.Status))
            .Where(f => search.Length == 0 ||
                        f.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        VisibleFiles = filtered;
        FileTree = FileTreeNode.Build(filtered, RightPath);
        OnPropertyChanged(nameof(FileTree));
        SyncTreeSelection();

        if (SelectedFile is not null && !filtered.Contains(SelectedFile))
            SelectedFile = null;
    }

    private void SyncTreeSelection()
    {
        if (SelectedFile is null)
            return;

        foreach (var node in FileTree.SelectMany(root => root.Flatten()))
        {
            if (ReferenceEquals(node.File, SelectedFile))
            {
                node.IsSelected = true;
                return;
            }
        }
    }

    private async Task LoadDiffAsync(FileItemViewModel? item)
    {
        var version = ++_diffVersion;
        _changeStarts = Array.Empty<int>();
        _currentChange = -1;
        ScrollToRow = -1;
        DiffStats = string.Empty;
        _selectedLeftText = string.Empty;
        _selectedRightText = string.Empty;

        if (item is null)
        {
            DiffRows = Array.Empty<DiffRow>();
            DiffHeader = "Виберіть файл зі списку";
            return;
        }

        DiffHeader = $"{item.RelativePath}    {item.SizeText}";

        if (item.Entry.IsBinary)
        {
            DiffRows = _diffService.BuildMessage("Бінарний файл. Порівняння рядків недоступне.").Rows;
            return;
        }

        var biggest = Math.Max(item.Entry.LeftSize, item.Entry.RightSize);
        if (biggest > MaxDiffBytes)
        {
            DiffRows = _diffService.BuildMessage(
                $"Файл завеликий для порядкового порівняння: {FileItemViewModel.FormatSize(biggest)}. Ліміт 20 МБ.").Rows;
            return;
        }

        var entry = item.Entry;
        var sideBySide = IsSideBySide;
        var viewOptions = new DiffViewOptions(Whitespace, CollapseUnchanged, ContextLines);

        try
        {
            var loaded = await Task.Run(() =>
            {
                var leftText = entry.LeftFullPath is null
                    ? string.Empty
                    : FileContentService.NormalizeLineEndings(FileContentService.ReadText(entry.LeftFullPath));
                var rightText = entry.RightFullPath is null
                    ? string.Empty
                    : FileContentService.NormalizeLineEndings(FileContentService.ReadText(entry.RightFullPath));

                var document = sideBySide
                    ? _diffService.BuildSideBySide(leftText, rightText, viewOptions)
                    : _diffService.BuildInline(leftText, rightText, viewOptions);

                return (leftText, rightText, document);
            });

            if (version != _diffVersion)
                return;

            _selectedLeftText = loaded.leftText;
            _selectedRightText = loaded.rightText;
            _changeStarts = loaded.document.ChangeStarts;
            DiffRows = loaded.document.Rows;
            DiffStats = $"+{loaded.document.AddedLines}  −{loaded.document.DeletedLines}";

            if (!loaded.document.HasVisibleDifferences && entry.Status == FileStatus.Modified)
                DiffStats = "відрізняються лише невидимі символи або кінці рядків";
        }
        catch (Exception ex)
        {
            if (version != _diffVersion)
                return;

            DiffRows = _diffService.BuildMessage($"Не вдалося прочитати файл: {ex.Message}").Rows;
        }
    }

    private static IReadOnlyList<string> ParsePatterns(string raw) =>
        raw.Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
