using System.Text.Json;

namespace FolderDiff.Services;

public sealed class AppSettings
{
    public string LeftPath { get; set; } = string.Empty;
    public string RightPath { get; set; } = string.Empty;
    public string IgnorePatterns { get; set; } = ".git;.vs;bin;obj;node_modules;packages;dist;*.user;*.suo";
    public int WhitespaceMode { get; set; }
    public bool IgnoreLineEndings { get; set; } = true;
    public bool IsSideBySide { get; set; } = true;
    public bool CollapseUnchanged { get; set; }
    public int ContextLines { get; set; } = 3;
    public bool WatchChanges { get; set; }

    public string ImagesFolder { get; set; } = string.Empty;
    public string RestoreFolder { get; set; } = string.Empty;
    public bool PackOnlyChanges { get; set; } = true;
    public int Parts { get; set; }
    public int Threads { get; set; }
    public int CoverBits { get; set; } = 1;
    public List<string> CoverFiles { get; set; } = new();
    public List<string> Presets { get; set; } = new();
    public int StepBits { get; set; } = 4;
    public int CompressionLevel { get; set; } = 11;
    public int ChunkMiB { get; set; }
    public string NamePrefix { get; set; } = "IMG";
    public string ExtraExcludes { get; set; } = string.Empty;
    public string SnapshotPath { get; set; } = string.Empty;
    public double DiffSplitWidth { get; set; }
    public double FileListWidth { get; set; } = 380;
    public bool GroupByFolder { get; set; } = true;
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FolderDiff",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
