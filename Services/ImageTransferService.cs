using CommunityToolkit.Mvvm.ComponentModel;
using FolderDiff.Models;
using ImageConverter;

namespace FolderDiff.Services;

public sealed class ImageTransferService : ObservableObject
{
    private KeySet? _key;
    private TransferKeyMode _mode = TransferKeyMode.Code;
    private string _pemFolder = string.Empty;
    private string? _pemPassword;

    public KeyStatus Status { get; private set; } =
        new(false, false, false, "Ключ не задано", null);

    public string CurrentCode { get; private set; } = string.Empty;

    public IReadOnlyList<string> PresetNames { get; } = PathFilter.Presets.Keys.ToList();

    public string KeyFilePath { get; } = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } dir ? dir : AppContext.BaseDirectory,
        EmbeddedKeys.FileName);

    public bool KeyFileExists => File.Exists(KeyFilePath);

    public KeyStatus InstallKeyFile(string sourceFile)
    {
        try
        {
            File.Copy(sourceFile, KeyFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            return Update(TransferKeyMode.Code, null, string.Empty,
                $"Не вдалося покласти файл поруч з програмою: {ex.Message}", null);
        }

        OnPropertyChanged(nameof(KeyFileExists));
        return TryAutoLoad();
    }

    public KeyStatus SetKey(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Update(TransferKeyMode.Code, null, string.Empty, "Ключ не задано", null);

        try
        {
            var trimmed = code.Trim();
            var key = KeyCode.Decode(trimmed);
            return Update(TransferKeyMode.Code, key, trimmed, Describe(key), "рядок ключа");
        }
        catch (Exception ex)
        {
            return Update(TransferKeyMode.Code, null, string.Empty, ex.Message, null);
        }
    }

    public KeyStatus TryAutoLoad()
    {
        try
        {
            var key = EmbeddedKeys.Resolve(null, out var source);
            if (key is null)
                return Update(TransferKeyMode.Code, null, string.Empty, "Файл imgkeys.txt не знайдено", null);

            return Update(TransferKeyMode.Code, key, KeyCode.Encode(key), Describe(key), source);
        }
        catch (Exception ex)
        {
            return Update(TransferKeyMode.Code, null, string.Empty, ex.Message, null);
        }
    }

    public KeyStatus UsePemFolder(string folder, string? password)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return Update(TransferKeyMode.PemFolder, null, string.Empty, "Папку з ключами не знайдено", null);

        _pemFolder = Path.GetFullPath(folder);
        _pemPassword = string.IsNullOrEmpty(password) ? null : password;

        var canPack = Paths.HasKeys(_pemFolder, forPacking: true);
        var canUnpack = Paths.HasKeys(_pemFolder, forPacking: false);

        if (!canPack && !canUnpack)
            return Update(TransferKeyMode.PemFolder, null, string.Empty, "У папці немає потрібних PEM-файлів", null);

        _mode = TransferKeyMode.PemFolder;
        _key = null;
        CurrentCode = string.Empty;
        Status = new KeyStatus(true, canPack, canUnpack, DescribePem(canPack, canUnpack), _pemFolder);
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(CurrentCode));
        return Status;
    }

    public (string First, string Second) CreatePair(string firstLabel, string secondLabel) =>
        KeyCode.CreatePair(
            string.IsNullOrWhiteSpace(firstLabel) ? "machine-a" : firstLabel,
            string.IsNullOrWhiteSpace(secondLabel) ? "machine-b" : secondLabel);

    public Task GeneratePemKeysAsync(string folder, string? password) =>
        Task.Run(() => KeyStore.GenerateKeyPairs(Path.GetFullPath(folder),
            string.IsNullOrEmpty(password) ? null : password));

    public IReadOnlyList<string> ExpandPresets(string names) => PathFilter.ExpandPresets(names);

    public IReadOnlyList<string> ReadPatternFile(string path) => PathFilter.ReadPatternFile(Path.GetFullPath(path));

    public bool HasRememberedVersion(string folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && StateStore.Exists(Path.GetFullPath(folder));

    public IReadOnlyList<string> PriorityOptions { get; } =
        new[] { string.Empty, "idle", "low", "below", "normal", "high" };

    public Task<PackReport> PackAsync(PackRequest request, IProgress<string> log) =>
        Task.Run(() =>
        {
            ApplyPriority(request.Priority, log);
            return ToReport(Packer.Run(BuildPackOptions(request), log.Report));
        });

    public Task<PackReport> PreviewAsync(PackRequest request, IProgress<string> log) =>
        Task.Run(() => ToReport(Packer.Preview(BuildPackOptions(request), log.Report)));

    private static void ApplyPriority(string priority, IProgress<string> log)
    {
        if (string.IsNullOrWhiteSpace(priority))
            return;

        var applied = ProcessTuning.Apply(priority);
        log.Report($"Пріоритет процесу: {applied}");
    }

    public Task<string> WriteSnapshotAsync(SnapshotRequest request, IProgress<string> log) =>
        Task.Run(() =>
        {
            var options = new PackOptions
            {
                InputDirectory = Path.GetFullPath(request.SourceFolder),
                SnapshotOutPath = Path.GetFullPath(request.SnapshotPath),
                ExcludePatterns = request.ExcludePatterns.ToList()
            };

            Packer.WriteSnapshot(options, log.Report);
            return options.SnapshotOutPath!;
        });

    public Task<UnpackReport> UnpackAsync(UnpackRequest request, IProgress<string> log) =>
        Task.Run(() =>
        {
            ApplyPriority(request.Priority, log);

            var options = new UnpackOptions
            {
                Inputs = new List<string> { Path.GetFullPath(request.ImagesFolder) },
                OutputDirectory = Path.GetFullPath(request.TargetFolder),
                SkipSignature = request.SkipSignature,
                RememberVersion = request.RememberVersion
            };

            ApplyUnpackKeys(options);

            var result = Unpacker.Run(options, log.Report);

            return new UnpackReport(
                result.SummaryMatches,
                result.ImageSummaryMatches,
                result.SignatureVerified,
                result.FileCount,
                result.DirectoryCount,
                result.Actual.OriginalBytes,
                result.IsDelta,
                result.Elapsed);
        });

    public Task<DeltaSummary> EstimateDeltaAsync(
        string baseFolder,
        string newFolder,
        IReadOnlyList<string> excludePatterns,
        IProgress<string> log) =>
        Task.Run(() =>
        {
            var patterns = excludePatterns.ToList();
            var filter = PathFilter.Build(patterns);

            log.Report($"Знімок базової папки: {baseFolder}");
            var snapshot = Snapshot.Build(Path.GetFullPath(baseFolder), filter, patterns, log: log.Report);

            log.Report($"Порівняння з {newFolder}");
            var stats = DeltaWriter.Preview(Path.GetFullPath(newFolder), snapshot, filter);

            log.Report("Оцінку завершено");
            return ToSummary(stats);
        });

    public Task<DeltaSummary> EstimateAgainstSnapshotAsync(
        string snapshotPath,
        string newFolder,
        IReadOnlyList<string> excludePatterns,
        IProgress<string> log) =>
        Task.Run(() =>
        {
            var patterns = excludePatterns.ToList();
            var filter = PathFilter.Build(patterns);

            log.Report($"Читаю знімок: {snapshotPath}");
            var snapshot = Snapshot.Load(Path.GetFullPath(snapshotPath));

            log.Report($"Порівняння з {newFolder}");
            var stats = DeltaWriter.Preview(Path.GetFullPath(newFolder), snapshot, filter);

            log.Report("Оцінку завершено");
            return ToSummary(stats);
        });

    private PackOptions BuildPackOptions(PackRequest request)
    {
        var options = new PackOptions
        {
            InputDirectory = Path.GetFullPath(request.SourceFolder),
            OutputDirectory = Path.GetFullPath(request.OutputFolder),
            ExcludePatterns = request.ExcludePatterns.ToList(),
            Parts = request.Parts,
            Threads = request.Threads,
            StepBits = request.StepBits,
            CompressionLevel = request.CompressionLevel,
            ChunkMiB = request.ChunkMiB,
            NamePrefix = string.IsNullOrWhiteSpace(request.NamePrefix) ? "IMG" : request.NamePrefix.Trim(),
            KeepArchive = request.KeepArchive,
            RememberVersion = request.RememberVersion,
            Covers = request.Covers.ToList(),
            CoverBits = request.CoverBits
        };

        switch (request.BaseMode)
        {
            case PackBaseMode.AgainstFolder:
                if (string.IsNullOrWhiteSpace(request.BaseFolder))
                    throw new InvalidOperationException("Для режиму «тільки зміни» потрібна базова папка.");
                options.AgainstDirectory = Path.GetFullPath(request.BaseFolder);
                break;
            case PackBaseMode.SnapshotFile:
                if (string.IsNullOrWhiteSpace(request.SnapshotPath))
                    throw new InvalidOperationException("Вкажіть файл знімка .icv.");
                options.BaseSnapshotPath = Path.GetFullPath(request.SnapshotPath);
                break;
            case PackBaseMode.RememberedVersion:
                options.UseDiff = true;
                break;
        }

        ApplyPackKeys(options);
        return options;
    }

    private void ApplyPackKeys(PackOptions options)
    {
        if (_mode == TransferKeyMode.PemFolder)
        {
            if (!Paths.HasKeys(_pemFolder, forPacking: true))
                throw new InvalidOperationException("У папці ключів немає файлів для упаковки.");

            options.RecipientPublicKeyPath = Path.Combine(_pemFolder, KeyStore.RecipientPublicFile);
            options.SenderPrivateKeyPath = Path.Combine(_pemFolder, KeyStore.SenderPrivateFile);
            options.KeyPassword = _pemPassword;
            return;
        }

        var key = RequireKey(forPacking: true);
        options.RecipientPublicMaterial = key.RecipientPublic;
        options.SenderPrivateMaterial = key.SenderPrivate;
    }

    private void ApplyUnpackKeys(UnpackOptions options)
    {
        if (_mode == TransferKeyMode.PemFolder)
        {
            if (!Paths.HasKeys(_pemFolder, forPacking: false))
                throw new InvalidOperationException("У папці ключів немає файлів для розпакування.");

            options.RecipientPrivateKeyPath = Path.Combine(_pemFolder, KeyStore.RecipientPrivateFile);
            options.SenderPublicKeyPath = Path.Combine(_pemFolder, KeyStore.SenderPublicFile);
            options.KeyPassword = _pemPassword;
            return;
        }

        var key = RequireKey(forPacking: false);
        options.RecipientPrivateMaterial = key.RecipientPrivate;
        options.SenderPublicMaterial = key.SenderPublic;
    }

    private KeySet RequireKey(bool forPacking)
    {
        if (_key is null)
            throw new InvalidOperationException("Ключ не задано. Відкрийте вкладку «Ключ» і вставте рядок ключа.");

        if (forPacking && !_key.CanPack)
            throw new InvalidOperationException("Цей ключ не містить матеріалу для упаковки.");

        if (!forPacking && !_key.CanUnpack)
            throw new InvalidOperationException("Цей ключ не містить матеріалу для розпакування.");

        return _key;
    }

    private KeyStatus Update(TransferKeyMode mode, KeySet? key, string code, string description, string? source)
    {
        _mode = mode;
        _key = key;
        CurrentCode = code;
        Status = new KeyStatus(key is not null, key?.CanPack ?? false, key?.CanUnpack ?? false, description, source);
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(CurrentCode));
        return Status;
    }

    private static string Describe(KeySet key)
    {
        var abilities = (key.CanPack, key.CanUnpack) switch
        {
            (true, true) => "може пакувати і розпаковувати",
            (true, false) => "може лише пакувати",
            (false, true) => "може лише розпаковувати",
            _ => "не містить потрібного матеріалу"
        };

        return key.Label.Length > 0 ? $"{key.Label}, {abilities}" : abilities;
    }

    private static string DescribePem(bool canPack, bool canUnpack) => (canPack, canUnpack) switch
    {
        (true, true) => "PEM-ключі: можна пакувати і розпаковувати",
        (true, false) => "PEM-ключі: можна лише пакувати",
        (false, true) => "PEM-ключі: можна лише розпаковувати",
        _ => "PEM-ключі неповні"
    };

    private static PackReport ToReport(PackResult result) => new(
        result.IsDelta,
        result.Images
            .Select(i => new TransferImage(i.Path, i.Width, i.Height, i.FileLength, i.PayloadLength))
            .ToList(),
        result.ImageBytes,
        result.SourceBytes,
        result.ArchiveBytes,
        result.CipherBytes,
        result.Summary.FileCount,
        result.Summary.DirectoryCount,
        result.Summary.OriginalBytes,
        result.Summary.TreeDigest.Length > 0 ? TreeDigest.Short(result.Summary.TreeDigest) : string.Empty,
        result.Elapsed,
        result.ReplacedImages,
        result.DeltaStats is null ? null : ToSummary(result.DeltaStats));

    private static DeltaSummary ToSummary(DeltaStats stats) => new(
        stats.KeptCount,
        stats.AddedCount,
        stats.PatchedCount,
        stats.DeletedFileCount,
        stats.DeletedDirectoryCount,
        stats.AddedDirectoryCount,
        stats.LiteralBytes,
        stats.CopiedBytes,
        stats.NewTotalBytes);
}
