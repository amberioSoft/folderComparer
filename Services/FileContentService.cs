using System.Text;
using FolderDiff.Models;

namespace FolderDiff.Services;

public static class FileContentService
{
    private const int ProbeSize = 8192;

    public static bool IsBinary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(ProbeSize, (int)Math.Min(stream.Length, ProbeSize))];
            var read = stream.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                    return true;
            }

            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static string ReadText(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string TrimLineEnds(string text)
    {
        var lines = NormalizeLineEndings(text).Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd();

        return string.Join('\n', lines);
    }

    public static string NormalizeForComparison(string text, WhitespaceMode mode, bool ignoreLineEndings)
    {
        var result = ignoreLineEndings || mode != WhitespaceMode.None
            ? NormalizeLineEndings(text)
            : text;

        if (mode == WhitespaceMode.None)
            return result;

        var lines = result.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = mode == WhitespaceMode.TrailingOnly ? lines[i].TrimEnd() : CollapseSpaces(lines[i]);

        return string.Join('\n', lines);
    }

    private static string CollapseSpaces(string line)
    {
        var builder = new StringBuilder(line.Length);
        var pendingSpace = false;

        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
