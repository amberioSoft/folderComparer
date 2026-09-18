using FolderDiff.Models;

namespace FolderDiff.ViewModels;

public sealed class FileItemViewModel
{
    public FileItemViewModel(FileEntry entry) => Entry = entry;

    public FileEntry Entry { get; }

    public string RelativePath => Entry.RelativePath;

    public FileStatus Status => Entry.Status;

    public string Name => Entry.Name;

    public string Folder => Entry.Folder;

    public string StatusGlyph => Status switch
    {
        FileStatus.Added => "+",
        FileStatus.Deleted => "−",
        FileStatus.Modified => "M",
        _ => "="
    };

    public string SizeText => Status switch
    {
        FileStatus.Added => FormatSize(Entry.RightSize),
        FileStatus.Deleted => FormatSize(Entry.LeftSize),
        _ => $"{FormatSize(Entry.LeftSize)} → {FormatSize(Entry.RightSize)}"
    };

    public string Tooltip => $"{RelativePath}{Environment.NewLine}{SizeText}{(Entry.IsBinary ? Environment.NewLine + "бінарний" : string.Empty)}";

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes / (1024.0 * 1024.0):0.##} МБ"
    };
}
