namespace FolderDiff.Models;

public static class ByteSize
{
    public static string Format(long bytes) => bytes switch
    {
        < 0 => "-",
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.##} МБ",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} ГБ"
    };
}
