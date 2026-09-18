using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FolderDiff.Infrastructure;

public static class ListBoxScroll
{
    public static readonly DependencyProperty ScrollToIndexProperty = DependencyProperty.RegisterAttached(
        "ScrollToIndex",
        typeof(int),
        typeof(ListBoxScroll),
        new PropertyMetadata(-1, OnScrollToIndexChanged));

    public static void SetScrollToIndex(DependencyObject element, int value) =>
        element.SetValue(ScrollToIndexProperty, value);

    public static int GetScrollToIndex(DependencyObject element) =>
        (int)element.GetValue(ScrollToIndexProperty);

    private static void OnScrollToIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox listBox || e.NewValue is not int index || index < 0)
            return;

        listBox.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Center(listBox, index)));
    }

    private static void Center(ListBox listBox, int index)
    {
        if (index >= listBox.Items.Count)
            return;

        listBox.ScrollIntoView(listBox.Items[index]);

        var viewer = FindScrollViewer(listBox);
        if (viewer is null)
            return;

        double viewport = viewer.ViewportHeight;
        if (viewport <= 0)
            return;

        double target = index - (viewport - 1) / 2;
        double highest = Math.Max(0, viewer.ScrollableHeight);
        viewer.ScrollToVerticalOffset(Math.Clamp(target, 0, highest));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
            return viewer;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        return null;
    }
}
