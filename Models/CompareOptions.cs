namespace FolderDiff.Models;

public sealed class CompareOptions
{
    public IReadOnlyList<string> IgnorePatterns { get; init; } = Array.Empty<string>();
    public WhitespaceMode Whitespace { get; init; } = WhitespaceMode.None;
    public bool IgnoreLineEndings { get; init; } = true;
}
