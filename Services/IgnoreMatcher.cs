using System.Text;
using System.Text.RegularExpressions;

namespace FolderDiff.Services;

public sealed class IgnoreMatcher
{
    private static readonly char PathSeparator = Path.DirectorySeparatorChar;

    private readonly List<Regex> _segmentRules = new();
    private readonly List<Regex> _pathRules = new();

    public static IgnoreMatcher Parse(IEnumerable<string> patterns)
    {
        var matcher = new IgnoreMatcher();
        foreach (var raw in patterns)
        {
            var pattern = raw.Trim().Replace(PathSeparator, '/').Trim('/');
            if (pattern.Length == 0)
                continue;

            var regex = new Regex(ToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (pattern.Contains('/'))
                matcher._pathRules.Add(regex);
            else
                matcher._segmentRules.Add(regex);
        }

        return matcher;
    }

    public bool IsIgnoredSegment(string segment)
    {
        foreach (var rule in _segmentRules)
        {
            if (rule.IsMatch(segment))
                return true;
        }

        return false;
    }

    public bool IsIgnoredPath(string relativePath)
    {
        if (_pathRules.Count == 0)
            return false;

        var normalized = relativePath.Replace(PathSeparator, '/');
        foreach (var rule in _pathRules)
        {
            if (rule.IsMatch(normalized))
                return true;
        }

        return false;
    }

    private static string ToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        foreach (var ch in pattern)
        {
            switch (ch)
            {
                case '*':
                    builder.Append("[^/]*");
                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                default:
                    builder.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }

        return builder.Append("$").ToString();
    }
}
