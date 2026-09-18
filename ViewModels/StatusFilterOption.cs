using FolderDiff.Models;

namespace FolderDiff.ViewModels;

public sealed class StatusFilterOption
{
    public StatusFilterOption(string title, Func<FileStatus, bool> match)
    {
        Title = title;
        Match = match;
    }

    public string Title { get; }

    public Func<FileStatus, bool> Match { get; }

    public override string ToString() => Title;

    public static IReadOnlyList<StatusFilterOption> CreateDefaults() => new[]
    {
        new StatusFilterOption("Тільки зміни", s => s != FileStatus.Unchanged),
        new StatusFilterOption("Усі файли", _ => true),
        new StatusFilterOption("Додані", s => s == FileStatus.Added),
        new StatusFilterOption("Видалені", s => s == FileStatus.Deleted),
        new StatusFilterOption("Змінені", s => s == FileStatus.Modified),
        new StatusFilterOption("Без змін", s => s == FileStatus.Unchanged)
    };
}
