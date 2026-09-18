using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FolderDiff.Models;

namespace FolderDiff.Infrastructure;

public static class DiffText
{
    public static readonly DependencyProperty SpansProperty = DependencyProperty.RegisterAttached(
        "Spans",
        typeof(IEnumerable<DiffSpan>),
        typeof(DiffText),
        new PropertyMetadata(null, OnSpansChanged));

    public static void SetSpans(DependencyObject element, IEnumerable<DiffSpan>? value) =>
        element.SetValue(SpansProperty, value);

    public static IEnumerable<DiffSpan>? GetSpans(DependencyObject element) =>
        (IEnumerable<DiffSpan>?)element.GetValue(SpansProperty);

    public static readonly DependencyProperty SelectableSpansProperty = DependencyProperty.RegisterAttached(
        "SelectableSpans",
        typeof(IEnumerable<DiffSpan>),
        typeof(DiffText),
        new PropertyMetadata(null, OnSelectableSpansChanged));

    public static void SetSelectableSpans(DependencyObject element, IEnumerable<DiffSpan>? value) =>
        element.SetValue(SelectableSpansProperty, value);

    public static IEnumerable<DiffSpan>? GetSelectableSpans(DependencyObject element) =>
        (IEnumerable<DiffSpan>?)element.GetValue(SelectableSpansProperty);

    private static void OnSelectableSpansChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box)
            return;

        var paragraph = new Paragraph { Margin = new Thickness(0) };

        if (e.NewValue is IEnumerable<DiffSpan> spans)
        {
            foreach (var span in spans)
            {
                var run = new Run(span.Text);
                var background = Brushes.Lookup(span.Kind);
                if (background is not null)
                    run.Background = background;

                paragraph.Inlines.Add(run);
            }
        }

        box.Document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = box.FontFamily,
            FontSize = box.FontSize
        };
    }

    private static void OnSpansChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
            return;

        block.Inlines.Clear();

        if (e.NewValue is not IEnumerable<DiffSpan> spans)
            return;

        foreach (var span in spans)
        {
            var run = new Run(span.Text);
            var background = Brushes.Lookup(span.Kind);
            if (background is not null)
                run.Background = background;

            block.Inlines.Add(run);
        }
    }

    private static class Brushes
    {
        private static Brush? _added;
        private static Brush? _deleted;

        public static Brush? Lookup(DiffSpanKind kind) => kind switch
        {
            DiffSpanKind.Added => _added ??= Find("WordAddedBrush"),
            DiffSpanKind.Deleted => _deleted ??= Find("WordDeletedBrush"),
            _ => null
        };

        private static Brush? Find(string key) =>
            Application.Current?.TryFindResource(key) as Brush;
    }
}
