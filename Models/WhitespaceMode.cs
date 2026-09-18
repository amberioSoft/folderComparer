namespace FolderDiff.Models;

public enum WhitespaceMode
{
    None,
    TrailingOnly,
    All
}

public sealed class WhitespaceOption
{
    public WhitespaceOption(string title, WhitespaceMode mode, string hint)
    {
        Title = title;
        Mode = mode;
        Hint = hint;
    }

    public string Title { get; }

    public WhitespaceMode Mode { get; }

    public string Hint { get; }

    public override string ToString() => Title;

    public static IReadOnlyList<WhitespaceOption> CreateDefaults() => new[]
    {
        new WhitespaceOption("Не ігнорувати", WhitespaceMode.None,
            "Будь-яка різниця в пробілах вважається зміною, як у git без прапорців"),
        new WhitespaceOption("Кінцеві пробіли", WhitespaceMode.TrailingOnly,
            "Пробіли в кінці рядка не вважаються зміною, як git --ignore-space-at-eol"),
        new WhitespaceOption("Усі пробіли", WhitespaceMode.All,
            "Відступи і пробіли всередині рядка не вважаються зміною, як git -w")
    };
}
