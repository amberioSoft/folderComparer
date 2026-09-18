namespace FolderDiff.Models;

public sealed class DiffRow
{
    public string LeftNumber { get; init; } = string.Empty;
    public DiffLineKind LeftKind { get; init; } = DiffLineKind.Filler;
    public IReadOnlyList<DiffSpan> LeftSpans { get; init; } = Array.Empty<DiffSpan>();

    public string RightNumber { get; init; } = string.Empty;
    public DiffLineKind RightKind { get; init; } = DiffLineKind.Filler;
    public IReadOnlyList<DiffSpan> RightSpans { get; init; } = Array.Empty<DiffSpan>();

    public string Marker { get; init; } = string.Empty;

    public bool IsSeparator { get; init; }
    public string SeparatorText { get; init; } = string.Empty;

    public bool IsChange =>
        !IsSeparator &&
        (LeftKind is DiffLineKind.Added or DiffLineKind.Deleted or DiffLineKind.Modified ||
         RightKind is DiffLineKind.Added or DiffLineKind.Deleted or DiffLineKind.Modified);

    public string LeftText => Flatten(LeftSpans);

    public string RightText => Flatten(RightSpans);

    private static string Flatten(IReadOnlyList<DiffSpan> spans) =>
        spans.Count == 0 ? string.Empty : string.Concat(spans.Select(s => s.Text));
}

public sealed record DiffViewOptions(WhitespaceMode Whitespace, bool CollapseUnchanged, int ContextLines);

public sealed record DiffDocument(
    IReadOnlyList<DiffRow> Rows,
    bool HasVisibleDifferences,
    int AddedLines,
    int DeletedLines,
    IReadOnlyList<int> ChangeStarts)
{
    public static DiffDocument Empty { get; } =
        new(Array.Empty<DiffRow>(), false, 0, 0, Array.Empty<int>());
}
