namespace FolderDiff.Models;

public enum DiffLineKind
{
    Filler,
    Unchanged,
    Modified,
    Added,
    Deleted
}

public enum DiffSpanKind
{
    Normal,
    Added,
    Deleted
}

public sealed record DiffSpan(string Text, DiffSpanKind Kind);
