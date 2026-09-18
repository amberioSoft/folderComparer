using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderDiff.Models;
using FolderDiff.Services;

namespace FolderDiff.ViewModels;

public partial class TransferViewModel : ObservableObject
{
    private const int MaxLogLines = 4000;

    private readonly ImageTransferService _transfer;
    private readonly IDialogService _dialogs;
    private readonly IComparePaths _paths;

    public TransferViewModel(ImageTransferService transfer, IDialogService dialogs, IComparePaths paths)
    {
        _transfer = transfer;
        _dialogs = dialogs;
        _paths = paths;

        foreach (var name in _transfer.PresetNames)
        {
            var option = new PresetOption(name);
            option.PropertyChanged += (_, _) => OnPropertyChanged(nameof(EffectiveExcludesText));
            Presets.Add(option);
        }

        _paths.PropertyChanged += OnPathsChanged;
        _transfer.PropertyChanged += OnTransferChanged;
    }

    public ObservableCollection<string> Log { get; } = new();

    public ObservableCollection<string> CoverFiles { get; } = new();

    public ObservableCollection<PresetOption> Presets { get; } = new();

    public IReadOnlyList<int> PartsOptions { get; } = new[] { 0, 1, 2, 3, 5, 8, 10, 16 };

    public IReadOnlyList<int> ThreadsOptions { get; } = new[] { 0, 2, 4, 6, 8, 12, 16 };

    public IReadOnlyList<int> StepBitsOptions { get; } = new[] { 2, 3, 4, 5, 6, 8 };

    public IReadOnlyList<int> LevelOptions { get; } = new[] { 1, 3, 5, 7, 9, 11 };

    public IReadOnlyList<int> ChunkOptions { get; } = new[] { 0, 8, 16, 32, 64 };

    public IReadOnlyList<int> CoverBitsOptions { get; } = new[] { 1, 2, 3, 4 };

    public IReadOnlyList<string> PriorityOptions => _transfer.PriorityOptions;

    public string SourceFolder => _paths.RightPath;

    public string BaseFolder => _paths.LeftPath;

    public bool CanPackWithKey => _transfer.Status.CanPack;

    public bool CanUnpackWithKey => _transfer.Status.CanUnpack;

    public string KeySummary => _transfer.Status.IsValid
        ? _transfer.Status.Description
        : $"{_transfer.Status.Description}. Відкрийте вкладку «Ключ».";

    public string EffectiveExcludesText
    {
        get
        {
            var list = EffectiveExcludes();
            return list.Count == 0 ? "нічого не виключено" : string.Join("   ", list);
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PackCommand))]
    [NotifyCanExecuteChangedFor(nameof(DryRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputFolderCommand))]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnpackCommand))]
    private string _imagesFolder = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnpackCommand))]
    private string _restoreFolder = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PackCommand))]
    [NotifyCanExecuteChangedFor(nameof(DryRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(EstimateCommand))]
    [NotifyPropertyChangedFor(nameof(BaseDescription))]
    private PackBaseMode _baseMode = PackBaseMode.AgainstFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PackCommand))]
    [NotifyCanExecuteChangedFor(nameof(EstimateCommand))]
    [NotifyPropertyChangedFor(nameof(BaseDescription))]
    private string _snapshotPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveExcludesText))]
    private string _extraExcludes = string.Empty;

    [ObservableProperty]
    private int _parts;

    [ObservableProperty]
    private int _threads;

    [ObservableProperty]
    private int _stepBits = 4;

    [ObservableProperty]
    private int _compressionLevel = 11;

    [ObservableProperty]
    private int _chunkMiB;

    [ObservableProperty]
    private string _namePrefix = "IMG";

    [ObservableProperty]
    private bool _keepArchive;

    [ObservableProperty]
    private bool _rememberVersion;

    [ObservableProperty]
    private int _coverBits = 1;

    [ObservableProperty]
    private bool _skipSignature;

    [ObservableProperty]
    private bool _rememberAfterUnpack;

    [ObservableProperty]
    private string _priority = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PackCommand))]
    [NotifyCanExecuteChangedFor(nameof(DryRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnpackCommand))]
    [NotifyCanExecuteChangedFor(nameof(EstimateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateSnapshotCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _operationTitle = string.Empty;

    [ObservableProperty]
    private PackReport? _packReport;

    [ObservableProperty]
    private UnpackReport? _unpackReport;

    [ObservableProperty]
    private DeltaSummary? _deltaEstimate;

    [ObservableProperty]
    private string _errorText = string.Empty;

    public string BaseDescription => BaseMode switch
    {
        PackBaseMode.Full => "Поїде вся папка, завжди 5 картинок.",
        PackBaseMode.AgainstFolder => string.IsNullOrWhiteSpace(BaseFolder)
            ? "Вкажіть ліву папку «До» угорі вікна."
            : $"База: {BaseFolder}",
        PackBaseMode.SnapshotFile => string.IsNullOrWhiteSpace(SnapshotPath)
            ? "Виберіть файл знімка .icv."
            : $"Знімок: {SnapshotPath}",
        _ => "Порівняння з версією, запамʼятованою в самій папці."
    };

    public void ApplySettings(AppSettings settings)
    {
        OutputFolder = settings.ImagesFolder;
        ImagesFolder = settings.ImagesFolder;
        RestoreFolder = settings.RestoreFolder;
        BaseMode = settings.PackOnlyChanges ? PackBaseMode.AgainstFolder : PackBaseMode.Full;
        Parts = PartsOptions.Contains(settings.Parts) ? settings.Parts : 0;
        Threads = ThreadsOptions.Contains(settings.Threads) ? settings.Threads : 0;
        CoverBits = CoverBitsOptions.Contains(settings.CoverBits) ? settings.CoverBits : 1;
        StepBits = StepBitsOptions.Contains(settings.StepBits) ? settings.StepBits : 4;
        CompressionLevel = LevelOptions.Contains(settings.CompressionLevel) ? settings.CompressionLevel : 11;
        ChunkMiB = ChunkOptions.Contains(settings.ChunkMiB) ? settings.ChunkMiB : 0;
        NamePrefix = string.IsNullOrWhiteSpace(settings.NamePrefix) ? "IMG" : settings.NamePrefix;
        ExtraExcludes = settings.ExtraExcludes;
        SnapshotPath = settings.SnapshotPath;

        CoverFiles.Clear();
        foreach (var file in settings.CoverFiles)
            CoverFiles.Add(file);

        foreach (var preset in Presets)
            preset.IsSelected = settings.Presets.Contains(preset.Name, StringComparer.OrdinalIgnoreCase);
    }

    public void WriteSettings(AppSettings settings)
    {
        settings.ImagesFolder = OutputFolder;
        settings.RestoreFolder = RestoreFolder;
        settings.PackOnlyChanges = BaseMode != PackBaseMode.Full;
        settings.Parts = Parts;
        settings.Threads = Threads;
        settings.CoverBits = CoverBits;
        settings.StepBits = StepBits;
        settings.CompressionLevel = CompressionLevel;
        settings.ChunkMiB = ChunkMiB;
        settings.NamePrefix = NamePrefix;
        settings.ExtraExcludes = ExtraExcludes;
        settings.SnapshotPath = SnapshotPath;
        settings.CoverFiles = CoverFiles.ToList();
        settings.Presets = Presets.Where(p => p.IsSelected).Select(p => p.Name).ToList();
    }

    private bool CanPack() =>
        !IsRunning && CanPackWithKey &&
        !string.IsNullOrWhiteSpace(SourceFolder) &&
        !string.IsNullOrWhiteSpace(OutputFolder) &&
        BaseIsReady();

    private bool CanUnpack() =>
        !IsRunning && CanUnpackWithKey &&
        !string.IsNullOrWhiteSpace(ImagesFolder) &&
        !string.IsNullOrWhiteSpace(RestoreFolder);

    private bool CanEstimate() =>
        !IsRunning &&
        !string.IsNullOrWhiteSpace(SourceFolder) &&
        BaseMode switch
        {
            PackBaseMode.AgainstFolder => !string.IsNullOrWhiteSpace(BaseFolder),
            PackBaseMode.SnapshotFile => !string.IsNullOrWhiteSpace(SnapshotPath),
            _ => false
        };

    private bool CanCreateSnapshot() => !IsRunning && !string.IsNullOrWhiteSpace(SourceFolder);

    private bool CanOpenOutput() => Directory.Exists(OutputFolder);

    private bool BaseIsReady() => BaseMode switch
    {
        PackBaseMode.AgainstFolder => !string.IsNullOrWhiteSpace(BaseFolder),
        PackBaseMode.SnapshotFile => !string.IsNullOrWhiteSpace(SnapshotPath),
        _ => true
    };

    [RelayCommand]
    private void BrowseOutput()
    {
        var picked = _dialogs.PickFolder("Куди покласти картинки", OutputFolder);
        if (picked is not null)
            OutputFolder = picked;
    }

    [RelayCommand]
    private void BrowseImages()
    {
        var picked = _dialogs.PickFolder("Папка з картинками", ImagesFolder);
        if (picked is not null)
            ImagesFolder = picked;
    }

    [RelayCommand]
    private void BrowseRestore()
    {
        var picked = _dialogs.PickFolder("Куди відновити папку", RestoreFolder);
        if (picked is not null)
            RestoreFolder = picked;
    }

    [RelayCommand]
    private void BrowseSnapshot()
    {
        var picked = _dialogs.PickFile("Файл знімка", "Знімок (*.icv)|*.icv|Усі файли (*.*)|*.*");
        if (picked is not null)
            SnapshotPath = picked;
    }

    [RelayCommand]
    private void UseRightAsRestore() => RestoreFolder = _paths.RightPath;

    [RelayCommand]
    private void UseOutputAsImages() => ImagesFolder = OutputFolder;

    [RelayCommand]
    private void AddCover()
    {
        var picked = _dialogs.PickPngFiles("Фото-обкладинки PNG");
        if (picked is null)
            return;

        foreach (var file in picked)
        {
            if (!CoverFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                CoverFiles.Add(file);
        }
    }

    [RelayCommand]
    private void ClearCovers() => CoverFiles.Clear();

    [RelayCommand]
    private void LoadExcludeFile()
    {
        var picked = _dialogs.PickFile(
            "Файл із шаблонами",
            "Текст (*.txt;*.gitignore)|*.txt;*.gitignore|Усі файли (*.*)|*.*");
        if (picked is null)
            return;

        try
        {
            var patterns = _transfer.ReadPatternFile(picked);
            var merged = ParseExtra().Concat(patterns).Distinct(StringComparer.OrdinalIgnoreCase);
            ExtraExcludes = string.Join(";", merged);
            Append($"Додано шаблонів з файлу: {patterns.Count}");
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    [RelayCommand]
    private void CopyLog() => _dialogs.CopyToClipboard(string.Join(Environment.NewLine, Log));

    [RelayCommand(CanExecute = nameof(CanOpenOutput))]
    private void OpenOutputFolder() => _dialogs.OpenInShell(OutputFolder);

    [RelayCommand(CanExecute = nameof(CanCreateSnapshot))]
    private Task CreateSnapshot()
    {
        var suggested = $"{new DirectoryInfo(SourceFolder).Name}-base.icv";
        var path = _dialogs.PickSaveFile("Зберегти знімок папки", suggested);
        if (path is null)
            return Task.CompletedTask;

        return RunAsync("Знімок папки", async progress =>
        {
            var written = await _transfer.WriteSnapshotAsync(
                new SnapshotRequest(SourceFolder, path, EffectiveExcludes()), progress);
            SnapshotPath = written;
            PackReport = null;
            UnpackReport = null;
        });
    }

    [RelayCommand(CanExecute = nameof(CanEstimate))]
    private Task Estimate() => RunAsync("Оцінка різниці", async progress =>
    {
        var summary = BaseMode == PackBaseMode.SnapshotFile
            ? await _transfer.EstimateAgainstSnapshotAsync(SnapshotPath, SourceFolder, EffectiveExcludes(), progress)
            : await _transfer.EstimateDeltaAsync(BaseFolder, SourceFolder, EffectiveExcludes(), progress);

        DeltaEstimate = summary;
        PackReport = null;
        UnpackReport = null;
    });

    [RelayCommand(CanExecute = nameof(CanPack))]
    private Task DryRun() => RunAsync("Пробний прогін", async progress =>
    {
        var report = await _transfer.PreviewAsync(BuildPackRequest(), progress);
        PackReport = report;
        UnpackReport = null;
        DeltaEstimate = report.Delta;
    });

    [RelayCommand(CanExecute = nameof(CanPack))]
    private Task Pack() => RunAsync("Упаковка в картинки", async progress =>
    {
        var report = await _transfer.PackAsync(BuildPackRequest(), progress);
        PackReport = report;
        UnpackReport = null;
        DeltaEstimate = report.Delta;
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
    });

    [RelayCommand(CanExecute = nameof(CanUnpack))]
    private Task Unpack() => RunAsync("Розпакування картинок", async progress =>
    {
        var report = await _transfer.UnpackAsync(
            new UnpackRequest(ImagesFolder, RestoreFolder, SkipSignature, RememberAfterUnpack, Priority), progress);
        UnpackReport = report;
        PackReport = null;
        DeltaEstimate = null;
    });

    private PackRequest BuildPackRequest() => new(
        SourceFolder,
        OutputFolder,
        BaseMode,
        BaseFolder,
        SnapshotPath,
        EffectiveExcludes(),
        Parts,
        Threads,
        StepBits,
        CompressionLevel,
        ChunkMiB,
        NamePrefix,
        KeepArchive,
        RememberVersion,
        CoverFiles.ToList(),
        CoverBits,
        Priority);

    private IReadOnlyList<string> EffectiveExcludes()
    {
        var result = new List<string>(_paths.ExcludePatterns);

        foreach (var preset in Presets.Where(p => p.IsSelected))
            result.AddRange(_transfer.ExpandPresets(preset.Name));

        result.AddRange(ParseExtra());

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IEnumerable<string> ParseExtra() =>
        ExtraExcludes.Split(
            new[] { ';', ',', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task RunAsync(string title, Func<IProgress<string>, Task> action)
    {
        IsRunning = true;
        OperationTitle = title;
        ErrorText = string.Empty;
        Append($"=== {title} ===");

        var progress = new Progress<string>(Append);

        try
        {
            await action(progress);
            Append($"=== {title}: готово ===");
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Append($"ПОМИЛКА: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            OperationTitle = string.Empty;
        }
    }

    private void Append(string line)
    {
        Log.Add(line);
        while (Log.Count > MaxLogLines)
            Log.RemoveAt(0);
    }

    private void OnPathsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IComparePaths.RightPath))
        {
            OnPropertyChanged(nameof(SourceFolder));
            PackCommand.NotifyCanExecuteChanged();
            DryRunCommand.NotifyCanExecuteChanged();
            EstimateCommand.NotifyCanExecuteChanged();
            CreateSnapshotCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName == nameof(IComparePaths.LeftPath))
        {
            OnPropertyChanged(nameof(BaseFolder));
            OnPropertyChanged(nameof(BaseDescription));
            PackCommand.NotifyCanExecuteChanged();
            DryRunCommand.NotifyCanExecuteChanged();
            EstimateCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName == nameof(IComparePaths.ExcludePatterns))
            OnPropertyChanged(nameof(EffectiveExcludesText));
    }

    private void OnTransferChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanPackWithKey));
        OnPropertyChanged(nameof(CanUnpackWithKey));
        OnPropertyChanged(nameof(KeySummary));
        PackCommand.NotifyCanExecuteChanged();
        DryRunCommand.NotifyCanExecuteChanged();
        UnpackCommand.NotifyCanExecuteChanged();
    }
}
