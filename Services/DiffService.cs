using System.Text;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using FolderDiff.Models;

namespace FolderDiff.Services;

public sealed class DiffService
{
    private const string Tab = "    ";

    public DiffDocument BuildSideBySide(string oldText, string newText, DiffViewOptions options)
    {
        var prepared = Prepare(oldText, newText, options.Whitespace);
        var model = SideBySideDiffBuilder.Diff(prepared.Old, prepared.New, options.Whitespace == WhitespaceMode.All, false);
        var count = Math.Max(model.OldText.Lines.Count, model.NewText.Lines.Count);
        var rows = new List<DiffRow>(count);

        for (var i = 0; i < count; i++)
        {
            var oldLine = i < model.OldText.Lines.Count ? model.OldText.Lines[i] : null;
            var newLine = i < model.NewText.Lines.Count ? model.NewText.Lines[i] : null;

            rows.Add(new DiffRow
            {
                LeftNumber = FormatPosition(oldLine),
                LeftKind = MapKind(oldLine),
                LeftSpans = BuildSpans(oldLine, DiffSpanKind.Deleted),
                RightNumber = FormatPosition(newLine),
                RightKind = MapKind(newLine),
                RightSpans = BuildSpans(newLine, DiffSpanKind.Added)
            });
        }

        var added = model.NewText.Lines.Count(l => l.Type is ChangeType.Inserted or ChangeType.Modified);
        var deleted = model.OldText.Lines.Count(l => l.Type is ChangeType.Deleted or ChangeType.Modified);

        return Finish(rows, added, deleted, options);
    }

    public DiffDocument BuildInline(string oldText, string newText, DiffViewOptions options)
    {
        var prepared = Prepare(oldText, newText, options.Whitespace);
        var model = InlineDiffBuilder.Diff(prepared.Old, prepared.New, options.Whitespace == WhitespaceMode.All, false);
        var rows = new List<DiffRow>(model.Lines.Count);
        var oldNumber = 0;
        var newNumber = 0;
        var added = 0;
        var deleted = 0;

        foreach (var line in model.Lines)
        {
            string leftNumber;
            string rightNumber;
            string marker;
            DiffLineKind kind;

            switch (line.Type)
            {
                case ChangeType.Inserted:
                    newNumber++;
                    added++;
                    leftNumber = string.Empty;
                    rightNumber = newNumber.ToString();
                    marker = "+";
                    kind = DiffLineKind.Added;
                    break;
                case ChangeType.Deleted:
                    oldNumber++;
                    deleted++;
                    leftNumber = oldNumber.ToString();
                    rightNumber = string.Empty;
                    marker = "-";
                    kind = DiffLineKind.Deleted;
                    break;
                case ChangeType.Imaginary:
                    continue;
                default:
                    oldNumber++;
                    newNumber++;
                    leftNumber = oldNumber.ToString();
                    rightNumber = newNumber.ToString();
                    marker = " ";
                    kind = DiffLineKind.Unchanged;
                    break;
            }

            rows.Add(new DiffRow
            {
                LeftNumber = leftNumber,
                RightNumber = rightNumber,
                Marker = marker,
                LeftKind = kind,
                LeftSpans = new[] { new DiffSpan(Expand(line.Text ?? string.Empty), DiffSpanKind.Normal) }
            });
        }

        return Finish(rows, added, deleted, options);
    }

    public string BuildUnifiedText(string oldText, string newText, WhitespaceMode whitespace)
    {
        var prepared = Prepare(oldText, newText, whitespace);
        var model = InlineDiffBuilder.Diff(prepared.Old, prepared.New, whitespace == WhitespaceMode.All, false);
        var builder = new StringBuilder();

        foreach (var line in model.Lines)
        {
            var prefix = line.Type switch
            {
                ChangeType.Inserted => "+",
                ChangeType.Deleted => "-",
                ChangeType.Imaginary => null,
                _ => " "
            };

            if (prefix is null)
                continue;

            builder.Append(prefix).AppendLine(line.Text ?? string.Empty);
        }

        return builder.ToString();
    }

    public DiffDocument BuildMessage(string message) =>
        new(new[]
        {
            new DiffRow
            {
                LeftKind = DiffLineKind.Unchanged,
                LeftSpans = new[] { new DiffSpan(message, DiffSpanKind.Normal) }
            }
        }, false, 0, 0, Array.Empty<int>());

    private static DiffDocument Finish(List<DiffRow> rows, int added, int deleted, DiffViewOptions options)
    {
        var hasDifferences = rows.Any(r => r.IsChange);
        var visible = options.CollapseUnchanged && hasDifferences
            ? Collapse(rows, Math.Max(0, options.ContextLines))
            : rows;

        return new DiffDocument(visible, hasDifferences, added, deleted, FindChangeStarts(visible));
    }

    private static IReadOnlyList<DiffRow> Collapse(List<DiffRow> rows, int context)
    {
        var keep = new bool[rows.Count];

        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].IsChange)
                continue;

            var from = Math.Max(0, i - context);
            var to = Math.Min(rows.Count - 1, i + context);
            for (var j = from; j <= to; j++)
                keep[j] = true;
        }

        var result = new List<DiffRow>(rows.Count);
        var index = 0;

        while (index < rows.Count)
        {
            if (keep[index])
            {
                result.Add(rows[index]);
                index++;
                continue;
            }

            var start = index;
            while (index < rows.Count && !keep[index])
                index++;

            var skipped = index - start;
            result.Add(new DiffRow
            {
                IsSeparator = true,
                SeparatorText = $"@@ згорнуто рядків без змін: {skipped} @@"
            });
        }

        return result;
    }

    private static IReadOnlyList<int> FindChangeStarts(IReadOnlyList<DiffRow> rows)
    {
        var starts = new List<int>();

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].IsChange && (i == 0 || !rows[i - 1].IsChange))
                starts.Add(i);
        }

        return starts;
    }

    private static string FormatPosition(DiffPiece? piece) =>
        piece?.Position is { } position ? position.ToString() : string.Empty;

    private static DiffLineKind MapKind(DiffPiece? piece) => piece?.Type switch
    {
        ChangeType.Inserted => DiffLineKind.Added,
        ChangeType.Deleted => DiffLineKind.Deleted,
        ChangeType.Modified => DiffLineKind.Modified,
        ChangeType.Unchanged => DiffLineKind.Unchanged,
        _ => DiffLineKind.Filler
    };

    private static IReadOnlyList<DiffSpan> BuildSpans(DiffPiece? piece, DiffSpanKind highlight)
    {
        if (piece is null || piece.Type == ChangeType.Imaginary)
            return Array.Empty<DiffSpan>();

        if (piece.Type != ChangeType.Modified || piece.SubPieces.Count == 0)
            return new[] { new DiffSpan(Expand(piece.Text ?? string.Empty), DiffSpanKind.Normal) };

        var spans = new List<DiffSpan>(piece.SubPieces.Count);
        foreach (var sub in piece.SubPieces)
        {
            if (sub.Type == ChangeType.Imaginary || string.IsNullOrEmpty(sub.Text))
                continue;

            var kind = sub.Type is ChangeType.Inserted or ChangeType.Deleted or ChangeType.Modified
                ? highlight
                : DiffSpanKind.Normal;
            spans.Add(new DiffSpan(Expand(sub.Text), kind));
        }

        return spans.Count > 0 ? spans : new[] { new DiffSpan(string.Empty, DiffSpanKind.Normal) };
    }

    private static (string Old, string New) Prepare(string oldText, string newText, WhitespaceMode mode) =>
        mode == WhitespaceMode.TrailingOnly
            ? (FileContentService.TrimLineEnds(oldText), FileContentService.TrimLineEnds(newText))
            : (oldText, newText);

    private static string Expand(string text) => text.Replace("\t", Tab);
}
