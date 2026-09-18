namespace FolderDiff.Models;

public sealed class FileEntry
{
    public required string RelativePath { get; init; }
    public required FileStatus Status { get; init; }
    public string? LeftFullPath { get; init; }
    public string? RightFullPath { get; init; }
    public long LeftSize { get; init; }
    public long RightSize { get; init; }
    public bool IsBinary { get; init; }

    public string Name => Path.GetFileName(RelativePath);
    public string Folder => Path.GetDirectoryName(RelativePath) is { Length: > 0 } d ? d : ".";
    public long SizeDelta => RightSize - LeftSize;
}
