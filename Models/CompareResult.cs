namespace FolderDiff.Models;

public sealed class CompareResult
{
    public required IReadOnlyList<FileEntry> Entries { get; init; }
    public required string LeftRoot { get; init; }
    public required string RightRoot { get; init; }
    public TimeSpan Elapsed { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public int AddedCount => Entries.Count(e => e.Status == FileStatus.Added);
    public int DeletedCount => Entries.Count(e => e.Status == FileStatus.Deleted);
    public int ModifiedCount => Entries.Count(e => e.Status == FileStatus.Modified);
    public int UnchangedCount => Entries.Count(e => e.Status == FileStatus.Unchanged);
}
