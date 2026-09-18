using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using FolderDiff.Models;

namespace FolderDiff.Services;

public sealed record CompareProgress(int Done, int Total, string Message);

public sealed class FolderComparer
{
    public Task<CompareResult> CompareAsync(
        string leftRoot,
        string rightRoot,
        CompareOptions options,
        IProgress<CompareProgress>? progress,
        CancellationToken token)
        => Task.Run(() => Compare(leftRoot, rightRoot, options, progress, token), token);

    private static CompareResult Compare(
        string leftRoot,
        string rightRoot,
        CompareOptions options,
        IProgress<CompareProgress>? progress,
        CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var matcher = IgnoreMatcher.Parse(options.IgnorePatterns);
        var errors = new ConcurrentBag<string>();

        leftRoot = Path.GetFullPath(leftRoot);
        rightRoot = Path.GetFullPath(rightRoot);

        progress?.Report(new CompareProgress(0, 0, "Сканування лівої папки..."));
        var left = Scan(leftRoot, matcher, errors, token);

        progress?.Report(new CompareProgress(0, 0, "Сканування правої папки..."));
        var right = Scan(rightRoot, matcher, errors, token);

        var allPaths = new HashSet<string>(left.Keys, StringComparer.OrdinalIgnoreCase);
        allPaths.UnionWith(right.Keys);

        var total = allPaths.Count;
        var done = 0;
        var entries = new ConcurrentBag<FileEntry>();

        progress?.Report(new CompareProgress(0, total, $"Порівняння файлів: {total}"));

        Parallel.ForEach(
            allPaths,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount },
            relativePath =>
            {
                var hasLeft = left.TryGetValue(relativePath, out var leftInfo);
                var hasRight = right.TryGetValue(relativePath, out var rightInfo);

                FileEntry entry;
                if (hasLeft && !hasRight)
                {
                    entry = new FileEntry
                    {
                        RelativePath = relativePath,
                        Status = FileStatus.Deleted,
                        LeftFullPath = leftInfo!.FullName,
                        LeftSize = SafeLength(leftInfo),
                        IsBinary = FileContentService.IsBinary(leftInfo.FullName)
                    };
                }
                else if (!hasLeft && hasRight)
                {
                    entry = new FileEntry
                    {
                        RelativePath = relativePath,
                        Status = FileStatus.Added,
                        RightFullPath = rightInfo!.FullName,
                        RightSize = SafeLength(rightInfo),
                        IsBinary = FileContentService.IsBinary(rightInfo.FullName)
                    };
                }
                else
                {
                    var isBinary = FileContentService.IsBinary(leftInfo!.FullName)
                                   || FileContentService.IsBinary(rightInfo!.FullName);
                    var equal = AreEqual(leftInfo, rightInfo!, isBinary, options, errors);
                    entry = new FileEntry
                    {
                        RelativePath = relativePath,
                        Status = equal ? FileStatus.Unchanged : FileStatus.Modified,
                        LeftFullPath = leftInfo.FullName,
                        RightFullPath = rightInfo!.FullName,
                        LeftSize = SafeLength(leftInfo),
                        RightSize = SafeLength(rightInfo),
                        IsBinary = isBinary
                    };
                }

                entries.Add(entry);

                var current = Interlocked.Increment(ref done);
                if (current % 25 == 0 || current == total)
                    progress?.Report(new CompareProgress(current, total, $"Порівняння: {current} з {total}"));
            });

        var ordered = entries
            .OrderBy(e => e.Status == FileStatus.Unchanged)
            .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CompareResult
        {
            Entries = ordered,
            LeftRoot = leftRoot,
            RightRoot = rightRoot,
            Elapsed = stopwatch.Elapsed,
            Errors = errors.Distinct().Take(50).ToList()
        };
    }

    private static long SafeLength(FileInfo info)
    {
        try
        {
            return info.Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static bool AreEqual(
        FileInfo left,
        FileInfo right,
        bool isBinary,
        CompareOptions options,
        ConcurrentBag<string> errors)
    {
        try
        {
            if (SafeLength(left) == SafeLength(right) && HashFile(left.FullName) == HashFile(right.FullName))
                return true;

            if (isBinary || (options.Whitespace == WhitespaceMode.None && !options.IgnoreLineEndings))
                return false;

            var leftText = FileContentService.NormalizeForComparison(
                FileContentService.ReadText(left.FullName), options.Whitespace, options.IgnoreLineEndings);
            var rightText = FileContentService.NormalizeForComparison(
                FileContentService.ReadText(right.FullName), options.Whitespace, options.IgnoreLineEndings);
            return string.Equals(leftText, rightText, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{left.FullName}: {ex.Message}");
            return false;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Dictionary<string, FileInfo> Scan(
        string root,
        IgnoreMatcher matcher,
        ConcurrentBag<string> errors,
        CancellationToken token)
    {
        var result = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            errors.Add($"Папка не існує: {root}");
            return result;
        }

        var enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = stack.Pop();

            try
            {
                var directory = new DirectoryInfo(current);

                foreach (var file in directory.EnumerateFiles("*", enumerationOptions))
                {
                    if (matcher.IsIgnoredSegment(file.Name))
                        continue;

                    var relative = Path.GetRelativePath(root, file.FullName);
                    if (matcher.IsIgnoredPath(relative))
                        continue;

                    result[relative] = file;
                }

                foreach (var child in directory.EnumerateDirectories("*", enumerationOptions))
                {
                    if (matcher.IsIgnoredSegment(child.Name))
                        continue;

                    var relative = Path.GetRelativePath(root, child.FullName);
                    if (matcher.IsIgnoredPath(relative))
                        continue;

                    stack.Push(child.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{current}: {ex.Message}");
            }
        }

        return result;
    }
}
