using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using ImageConverter;
using Microsoft.Win32;

namespace FolderDiff;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    [STAThread]
    public static void Main(string[] args)
    {
        var app = new App { Resources = Theme.Create(), ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Startup += (_, e) => app.OnStarted(e.Args.Length > 0 ? e.Args : args);
        app.Exit += (_, _) => app.OnStopped();
        app.Run();
    }

    private void OnStarted(string[] args)
    {
        _viewModel = new MainViewModel(new DialogService(), new SettingsService(), new ImageTransferService());

        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Show();

        if (args.Length < 2)
            return;

        _viewModel.LeftPath = args[0];
        _viewModel.RightPath = args[1];

        if (_viewModel.CompareCommand.CanExecute(null))
            _viewModel.CompareCommand.Execute(null);
    }

    private void OnStopped()
    {
        _viewModel?.PersistSettings();
        _viewModel?.Dispose();
    }
}

public static class Theme
{
    public static ResourceDictionary Create()
    {
        var resources = new ResourceDictionary();

        Add(resources, "ChromeBrush", "#F6F8FA");
        Add(resources, "SurfaceBrush", "#FFFFFF");
        Add(resources, "BorderLineBrush", "#D0D7DE");
        Add(resources, "GutterBrush", "#EFF2F5");
        Add(resources, "LineNumberBrush", "#8C959F");
        Add(resources, "MutedTextBrush", "#6E7781");
        Add(resources, "LineAddedBrush", "#E6FFEC");
        Add(resources, "LineDeletedBrush", "#FFEBE9");
        Add(resources, "LineFillerBrush", "#FAFBFC");
        Add(resources, "WordAddedBrush", "#A7F0BA");
        Add(resources, "WordDeletedBrush", "#FFC9C6");
        Add(resources, "StatusAddedBrush", "#1A7F37");
        Add(resources, "StatusDeletedBrush", "#CF222E");
        Add(resources, "StatusModifiedBrush", "#9A6700");
        Add(resources, "StatusUnchangedBrush", "#8C959F");
        Add(resources, "SplitterBrush", "#E3E7EB");
        Add(resources, "SplitterHoverBrush", "#9BB4D4");
        Add(resources, "TreeSelectionBrush", "#CDE6F7");
        Add(resources, "TreeSelectionInactiveBrush", "#EDEDF0");
        Add(resources, "TreeHoverBrush", "#E8F1FB");
        Add(resources, "TreeGlyphBrush", "#5A5A5C");
        Add(resources, "TreeGlyphHoverBrush", "#005FB8");
        Add(resources, "FolderIconBrush", "#DCB67A");
        Add(resources, "FileIconBrush", "#8A8A8D");
        Add(resources, "TreePathBrush", "#23497A");

        resources["MonoFont"] = new FontFamily("Cascadia Mono, Consolas, Courier New");
        resources["FolderGeometry"] = Geometry.Parse("M 1,2.5 L 5.4,2.5 L 6.9,4.2 L 15,4.2 L 15,13.5 L 1,13.5 Z");
        resources["FileGeometry"] = Geometry.Parse("M 3.5,1.5 L 9.5,1.5 L 12.5,4.5 L 12.5,14.5 L 3.5,14.5 Z");

        return resources;
    }

    private static void Add(ResourceDictionary resources, string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        resources[key] = brush;
    }
}

public partial class MainWindow : Window
{
    private const double MinPaneWidth = 160;
    private const double MinFileListWidth = 200;
    private const double SplitterWidth = 6;

    private bool _paneSizedByUser;
    private INotifyCollectionChanged? _log;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnWindowLoaded;
        SizeChanged += OnWindowSizeChanged;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _paneSizedByUser = ViewModel is { DiffSplitWidth: > 0 };

        if (LogList.ItemsSource is INotifyCollectionChanged log)
        {
            _log = log;
            _log.CollectionChanged += OnLogChanged;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0)
            return;

        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void OnPathDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnLeftDrop(object sender, DragEventArgs e)
    {
        if (TryGetFolder(e, out var folder) && ViewModel is { } viewModel)
            viewModel.LeftPath = folder;

        e.Handled = true;
    }

    private void OnRightDrop(object sender, DragEventArgs e)
    {
        if (TryGetFolder(e, out var folder) && ViewModel is { } viewModel)
            viewModel.RightPath = folder;

        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (ItemsControl.ContainerFromElement((ListBox)sender, source) is ListBoxItem item)
            item.IsSelected = true;
    }

    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ViewModel is { } viewModel && e.NewValue is FileTreeNode { File: { } file })
            viewModel.SelectedFile = file;
    }

    private void OnPaneSplitterDragCompleted(object sender, DragCompletedEventArgs e) => _paneSizedByUser = true;

    private void OnPaneTitlesSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        var total = PaneTitles.ActualWidth;
        if (total < 2 * MinPaneWidth + SplitterWidth)
            return;

        if (!_paneSizedByUser)
        {
            viewModel.DiffSplitWidth = (total - SplitterWidth) / 2;
            return;
        }

        var maximum = total - MinPaneWidth - SplitterWidth;
        if (viewModel.DiffSplitWidth > maximum)
            viewModel.DiffSplitWidth = maximum;
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        var maximum = ActualWidth - 400;
        if (maximum < MinFileListWidth)
            return;

        if (viewModel.FileListWidth > maximum)
            viewModel.FileListWidth = maximum;
    }

    private static bool TryGetFolder(DragEventArgs e, out string folder)
    {
        folder = string.Empty;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } items)
            return false;

        var candidate = items[0];
        folder = Directory.Exists(candidate)
            ? candidate
            : Path.GetDirectoryName(candidate) ?? string.Empty;

        return folder.Length > 0;
    }
}

public sealed class DiffLineBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isRight = string.Equals(parameter as string, "Right", StringComparison.OrdinalIgnoreCase);

        var key = value switch
        {
            DiffLineKind.Added => "LineAddedBrush",
            DiffLineKind.Deleted => "LineDeletedBrush",
            DiffLineKind.Modified => isRight ? "LineAddedBrush" : "LineDeletedBrush",
            DiffLineKind.Filler => "LineFillerBrush",
            _ => null
        };

        if (key is null)
            return Brushes.Transparent;

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            FileStatus.Added => "StatusAddedBrush",
            FileStatus.Deleted => "StatusDeletedBrush",
            FileStatus.Modified => "StatusModifiedBrush",
            _ => "StatusUnchangedBrush"
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert)
            flag = !flag;

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class DiffRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? RowTemplate { get; set; }

    public DataTemplate? SeparatorTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is DiffRow { IsSeparator: true } ? SeparatorTemplate : RowTemplate;
}

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

public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && parameter is not null &&
        string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string name)
        {
            var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (type.IsEnum)
                return Enum.Parse(type, name);
        }

        return Binding.DoNothing;
    }
}

public sealed class AutoNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int number && number == 0 ? "авто" : value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public string TrueKey { get; set; } = "StatusAddedBrush";

    public string FalseKey { get; set; } = "StatusDeletedBrush";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is true ? TrueKey : FalseKey;
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class CheckMarkConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "✓" : "✗";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class PixelWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && width > 0
            ? new GridLength(width, GridUnitType.Pixel)
            : new GridLength(1, GridUnitType.Star);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is GridLength length ? length.Value : 0d;
}

public sealed class AutoWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && width > 0 ? width : double.NaN;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class EnumVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && parameter is not null &&
        string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

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

public sealed class CompareOptions
{
    public IReadOnlyList<string> IgnorePatterns { get; init; } = Array.Empty<string>();
    public WhitespaceMode Whitespace { get; init; } = WhitespaceMode.None;
    public bool IgnoreLineEndings { get; init; } = true;
}

public sealed class CompareResult
{
    public required IReadOnlyList<FileEntry> Entries { get; init; }
    public required string LeftRoot { get; init; }
    public required string RightRoot { get; init; }
    public TimeSpan Elapsed { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public int AddedCount => Entries.Count(e => e.Status == FileStatus.Added);
    public int DeletedCount => Entries.Count(e => e.Status == FileStatus.Deleted);
    public int ModifiedCount => Entries.Count(e => e.Status == FileStatus.Modified);
    public int UnchangedCount => Entries.Count(e => e.Status == FileStatus.Unchanged);
}

public enum DiffLineKind
{
    Filler,
    Unchanged,
    Modified,
    Added,
    Deleted
}

public enum DiffSpanKind
{
    Normal,
    Added,
    Deleted
}

public sealed record DiffSpan(string Text, DiffSpanKind Kind);

public sealed class DiffRow
{
    public string LeftNumber { get; init; } = string.Empty;
    public DiffLineKind LeftKind { get; init; } = DiffLineKind.Filler;
    public IReadOnlyList<DiffSpan> LeftSpans { get; init; } = Array.Empty<DiffSpan>();

    public string RightNumber { get; init; } = string.Empty;
    public DiffLineKind RightKind { get; init; } = DiffLineKind.Filler;
    public IReadOnlyList<DiffSpan> RightSpans { get; init; } = Array.Empty<DiffSpan>();

    public string Marker { get; init; } = string.Empty;

    public bool IsSeparator { get; init; }
    public string SeparatorText { get; init; } = string.Empty;

    public bool IsChange =>
        !IsSeparator &&
        (LeftKind is DiffLineKind.Added or DiffLineKind.Deleted or DiffLineKind.Modified ||
         RightKind is DiffLineKind.Added or DiffLineKind.Deleted or DiffLineKind.Modified);

    public string LeftText => Flatten(LeftSpans);

    public string RightText => Flatten(RightSpans);

    private static string Flatten(IReadOnlyList<DiffSpan> spans) =>
        spans.Count == 0 ? string.Empty : string.Concat(spans.Select(s => s.Text));
}

public sealed record DiffViewOptions(WhitespaceMode Whitespace, bool CollapseUnchanged, int ContextLines);

public sealed record DiffDocument(
    IReadOnlyList<DiffRow> Rows,
    bool HasVisibleDifferences,
    int AddedLines,
    int DeletedLines,
    IReadOnlyList<int> ChangeStarts)
{
    public static DiffDocument Empty { get; } =
        new(Array.Empty<DiffRow>(), false, 0, 0, Array.Empty<int>());
}

public sealed class FileEntry
{
    public required string RelativePath { get; init; }
    public required FileStatus Status { get; init; }
    public string? LeftFullPath { get; init; }
    public string? RightFullPath { get; init; }
    public long LeftSize { get; init; }
    public long RightSize { get; init; }
    public bool IsBinary { get; init; }

    public string Name => Path.GetFileName(RelativePath);
    public string Folder => Path.GetDirectoryName(RelativePath) is { Length: > 0 } d ? d : ".";
    public long SizeDelta => RightSize - LeftSize;
}

public enum FileStatus
{
    Unchanged,
    Modified,
    Added,
    Deleted
}

public sealed record TransferImage(string Path, int Width, int Height, long FileLength, long PayloadLength)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Resolution => $"{Width}x{Height}";

    public string FileLengthText => ByteSize.Format(FileLength);

    public string PayloadLengthText => ByteSize.Format(PayloadLength);
}

public sealed record DeltaSummary(
    int KeptCount,
    int AddedCount,
    int PatchedCount,
    int DeletedFileCount,
    int DeletedDirectoryCount,
    int AddedDirectoryCount,
    long LiteralBytes,
    long CopiedBytes,
    long NewTotalBytes)
{
    public string LiteralBytesText => ByteSize.Format(LiteralBytes);

    public string CopiedBytesText => ByteSize.Format(CopiedBytes);

    public string NewTotalBytesText => ByteSize.Format(NewTotalBytes);

    public string FilesText => $"без змін {KeptCount}, нових {AddedCount}, змінених {PatchedCount}";

    public string RemovedText => $"видалено файлів {DeletedFileCount}, папок {DeletedDirectoryCount}";
}

public sealed record PackReport(
    bool IsDelta,
    IReadOnlyList<TransferImage> Images,
    long ImageBytes,
    long SourceBytes,
    long ArchiveBytes,
    long CipherBytes,
    int FileCount,
    int DirectoryCount,
    long OriginalBytes,
    string TreeDigest,
    TimeSpan Elapsed,
    int ReplacedImages,
    DeltaSummary? Delta)
{
    public string ModeText => IsDelta ? "тільки зміни" : "повна копія";

    public string ImageBytesText => ByteSize.Format(ImageBytes);

    public string SourceBytesText => ByteSize.Format(SourceBytes);

    public string ArchiveBytesText => ByteSize.Format(ArchiveBytes);

    public string CipherBytesText => ByteSize.Format(CipherBytes);

    public string OriginalBytesText => ByteSize.Format(OriginalBytes);

    public string ContentText => $"файлів {FileCount}, папок {DirectoryCount}";

    public string ElapsedText => $"{Elapsed.TotalSeconds:0.00} с";

    public bool HasImages => Images.Count > 0;
}

public sealed record UnpackReport(
    bool SummaryMatches,
    bool ImageSummaryMatches,
    bool SignatureVerified,
    int FileCount,
    int DirectoryCount,
    long OriginalBytes,
    bool WasDelta,
    TimeSpan Elapsed)
{
    public string ModeText => WasDelta ? "застосовано набір змін" : "відновлено повну копію";

    public string ContentText => $"файлів {FileCount}, папок {DirectoryCount}";

    public string OriginalBytesText => ByteSize.Format(OriginalBytes);

    public string ElapsedText => $"{Elapsed.TotalSeconds:0.00} с";

    public bool AllChecksPassed => SummaryMatches && ImageSummaryMatches && SignatureVerified;
}

public sealed record KeyStatus(bool IsValid, bool CanPack, bool CanUnpack, string Description, string? Source);

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

public sealed class DialogService : IDialogService
{
    public string? PickFolder(string title, string? initialPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            dialog.InitialDirectory = initialPath;

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public IReadOnlyList<string>? PickPngFiles(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Multiselect = true,
            Filter = "Картинки PNG (*.png)|*.png"
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }

    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Multiselect = false,
            Filter = filter
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = defaultFileName,
            Filter = "Текстовий файл (*.txt)|*.txt"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void ShowError(string message) =>
        MessageBox.Show(message, "Folder Diff", MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowInfo(string message) =>
        MessageBox.Show(message, "Folder Diff", MessageBoxButton.OK, MessageBoxImage.Information);

    public void OpenInShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError($"Не вдалося відкрити файл:{Environment.NewLine}{ex.Message}");
        }
    }

    public void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        catch (Exception ex)
        {
            ShowError($"Не вдалося відкрити папку:{Environment.NewLine}{ex.Message}");
        }
    }

    public void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            ShowError($"Не вдалося скопіювати:{Environment.NewLine}{ex.Message}");
        }
    }
}

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

public sealed class FolderWatcher : IDisposable
{
    private const int DebounceMilliseconds = 800;

    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _debounce;

    public FolderWatcher() => _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

    public event EventHandler? Changed;

    public void Start(IEnumerable<string> roots)
    {
        Stop();

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size
            };

            watcher.Changed += OnFileSystemEvent;
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemEvent;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;

            _watchers.Add(watcher);
        }
    }

    public void Stop()
    {
        _debounce.Change(Timeout.Infinite, Timeout.Infinite);

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileSystemEvent;
            watcher.Created -= OnFileSystemEvent;
            watcher.Deleted -= OnFileSystemEvent;
            watcher.Renamed -= OnFileSystemEvent;
            watcher.Error -= OnError;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    public void Dispose()
    {
        Stop();
        _debounce.Dispose();
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => Schedule();

    private void OnError(object sender, ErrorEventArgs e) => Schedule();

    private void Schedule() => _debounce.Change(DebounceMilliseconds, Timeout.Infinite);

    private void Fire()
    {
        var handler = Changed;
        if (handler is null)
            return;

        if (_context is null)
            handler(this, EventArgs.Empty);
        else
            _context.Post(_ => handler(this, EventArgs.Empty), null);
    }
}

public interface IDialogService
{
    string? PickFolder(string title, string? initialPath);

    IReadOnlyList<string>? PickPngFiles(string title);

    string? PickFile(string title, string filter);

    string? PickSaveFile(string title, string defaultFileName);

    void ShowError(string message);

    void ShowInfo(string message);

    void OpenInShell(string path);

    void RevealInExplorer(string path);

    void CopyToClipboard(string text);
}

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

public sealed class ImageTransferService : ObservableObject
{
    private KeySet? _key;
    private TransferKeyMode _mode = TransferKeyMode.Code;
    private string _pemFolder = string.Empty;
    private string? _pemPassword;

    public KeyStatus Status { get; private set; } =
        new(false, false, false, "Ключ не задано", null);

    public string CurrentCode { get; private set; } = string.Empty;

    public IReadOnlyList<string> PresetNames { get; } = PathFilter.Presets.Keys.ToList();

    public string KeyFilePath { get; } = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } dir ? dir : AppContext.BaseDirectory,
        EmbeddedKeys.FileName);

    public bool KeyFileExists => File.Exists(KeyFilePath);

    public KeyStatus InstallKeyFile(string sourceFile)
    {
        try
        {
            File.Copy(sourceFile, KeyFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            return Update(TransferKeyMode.Code, null, string.Empty,
                $"Не вдалося покласти файл поруч з програмою: {ex.Message}", null);
        }

        OnPropertyChanged(nameof(KeyFileExists));
        return TryAutoLoad();
    }

    public KeyStatus SetKey(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Update(TransferKeyMode.Code, null, string.Empty, "Ключ не задано", null);

        try
        {
            var trimmed = code.Trim();
            var key = KeyCode.Decode(trimmed);
            return Update(TransferKeyMode.Code, key, trimmed, Describe(key), "рядок ключа");
        }
        catch (Exception ex)
        {
            return Update(TransferKeyMode.Code, null, string.Empty, ex.Message, null);
        }
    }

    public KeyStatus TryAutoLoad()
    {
        try
        {
            var key = EmbeddedKeys.Resolve(null, out var source);
            if (key is null)
                return Update(TransferKeyMode.Code, null, string.Empty, "Файл imgkeys.txt не знайдено", null);

            return Update(TransferKeyMode.Code, key, KeyCode.Encode(key), Describe(key), source);
        }
        catch (Exception ex)
        {
            return Update(TransferKeyMode.Code, null, string.Empty, ex.Message, null);
        }
    }

    public KeyStatus UsePemFolder(string folder, string? password)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return Update(TransferKeyMode.PemFolder, null, string.Empty, "Папку з ключами не знайдено", null);

        _pemFolder = Path.GetFullPath(folder);
        _pemPassword = string.IsNullOrEmpty(password) ? null : password;

        var canPack = Paths.HasKeys(_pemFolder, forPacking: true);
        var canUnpack = Paths.HasKeys(_pemFolder, forPacking: false);

        if (!canPack && !canUnpack)
            return Update(TransferKeyMode.PemFolder, null, string.Empty, "У папці немає потрібних PEM-файлів", null);

        _mode = TransferKeyMode.PemFolder;
        _key = null;
        CurrentCode = string.Empty;
        Status = new KeyStatus(true, canPack, canUnpack, DescribePem(canPack, canUnpack), _pemFolder);
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(CurrentCode));
        return Status;
    }

    public (string First, string Second) CreatePair(string firstLabel, string secondLabel) =>
        KeyCode.CreatePair(
            string.IsNullOrWhiteSpace(firstLabel) ? "machine-a" : firstLabel,
            string.IsNullOrWhiteSpace(secondLabel) ? "machine-b" : secondLabel);

    public Task GeneratePemKeysAsync(string folder, string? password) =>
        Task.Run(() => KeyStore.GenerateKeyPairs(Path.GetFullPath(folder),
            string.IsNullOrEmpty(password) ? null : password));

    public IReadOnlyList<string> ExpandPresets(string names) => PathFilter.ExpandPresets(names);

    public IReadOnlyList<string> ReadPatternFile(string path) => PathFilter.ReadPatternFile(Path.GetFullPath(path));

    public bool HasRememberedVersion(string folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && StateStore.Exists(Path.GetFullPath(folder));

    public IReadOnlyList<string> PriorityOptions { get; } =
        new[] { string.Empty, "idle", "low", "below", "normal", "high" };

    public Task<PackReport> PackAsync(PackRequest request, IProgress<string> log) =>
        Task.Run(() =>
        {
            ApplyPriority(request.Priority, log);
            return ToReport(Packer.Run(BuildPackOptions(request), log.Report));
        });

    public Task<PackReport> PreviewAsync(PackRequest request, IProgress<string> log) =>
        Task.Run(() => ToReport(Packer.Preview(BuildPackOptions(request), log.Report)));

    private static void ApplyPriority(string priority, IProgress<string> log)
    {
        if (string.IsNullOrWhiteSpace(priority))
            return;

        var applied = ProcessTuning.Apply(priority);
        log.Report($"Пріоритет процесу: {applied}");
    }

    public Task<string> WriteSnapshotAsync(SnapshotRequest request, IProgress<string> log) =>
        Task.Run(() =>
        {
            var options = new PackOptions
            {
                InputDirectory = Path.GetFullPath(request.SourceFolder),
                SnapshotOutPath = Path.GetFullPath(request.SnapshotPath),
                ExcludePatterns = request.ExcludePatterns.ToList()
            };

            Packer.WriteSnapshot(options, log.Report);
            return options.SnapshotOutPath!;
        });

    public Task<UnpackReport> UnpackAsync(UnpackRequest request, IProgress<string> log) =>
        Task.Run(() =>
        {
            ApplyPriority(request.Priority, log);

            var options = new UnpackOptions
            {
                Inputs = new List<string> { Path.GetFullPath(request.ImagesFolder) },
                OutputDirectory = Path.GetFullPath(request.TargetFolder),
                SkipSignature = request.SkipSignature,
                RememberVersion = request.RememberVersion
            };

            ApplyUnpackKeys(options);

            var result = Unpacker.Run(options, log.Report);

            return new UnpackReport(
                result.SummaryMatches,
                result.ImageSummaryMatches,
                result.SignatureVerified,
                result.FileCount,
                result.DirectoryCount,
                result.Actual.OriginalBytes,
                result.IsDelta,
                result.Elapsed);
        });

    public Task<DeltaSummary> EstimateDeltaAsync(
        string baseFolder,
        string newFolder,
        IReadOnlyList<string> excludePatterns,
        IProgress<string> log) =>
        Task.Run(() =>
        {
            var patterns = excludePatterns.ToList();
            var filter = PathFilter.Build(patterns);

            log.Report($"Знімок базової папки: {baseFolder}");
            var snapshot = Snapshot.Build(Path.GetFullPath(baseFolder), filter, patterns, log: log.Report);

            log.Report($"Порівняння з {newFolder}");
            var stats = DeltaWriter.Preview(Path.GetFullPath(newFolder), snapshot, filter);

            log.Report("Оцінку завершено");
            return ToSummary(stats);
        });

    public Task<DeltaSummary> EstimateAgainstSnapshotAsync(
        string snapshotPath,
        string newFolder,
        IReadOnlyList<string> excludePatterns,
        IProgress<string> log) =>
        Task.Run(() =>
        {
            var patterns = excludePatterns.ToList();
            var filter = PathFilter.Build(patterns);

            log.Report($"Читаю знімок: {snapshotPath}");
            var snapshot = Snapshot.Load(Path.GetFullPath(snapshotPath));

            log.Report($"Порівняння з {newFolder}");
            var stats = DeltaWriter.Preview(Path.GetFullPath(newFolder), snapshot, filter);

            log.Report("Оцінку завершено");
            return ToSummary(stats);
        });

    private PackOptions BuildPackOptions(PackRequest request)
    {
        var options = new PackOptions
        {
            InputDirectory = Path.GetFullPath(request.SourceFolder),
            OutputDirectory = Path.GetFullPath(request.OutputFolder),
            ExcludePatterns = request.ExcludePatterns.ToList(),
            Parts = request.Parts,
            Threads = request.Threads,
            StepBits = request.StepBits,
            CompressionLevel = request.CompressionLevel,
            ChunkMiB = request.ChunkMiB,
            NamePrefix = string.IsNullOrWhiteSpace(request.NamePrefix) ? "IMG" : request.NamePrefix.Trim(),
            KeepArchive = request.KeepArchive,
            RememberVersion = request.RememberVersion,
            Covers = request.Covers.ToList(),
            CoverBits = request.CoverBits
        };

        switch (request.BaseMode)
        {
            case PackBaseMode.AgainstFolder:
                if (string.IsNullOrWhiteSpace(request.BaseFolder))
                    throw new InvalidOperationException("Для режиму «тільки зміни» потрібна базова папка.");
                options.AgainstDirectory = Path.GetFullPath(request.BaseFolder);
                break;
            case PackBaseMode.SnapshotFile:
                if (string.IsNullOrWhiteSpace(request.SnapshotPath))
                    throw new InvalidOperationException("Вкажіть файл знімка .icv.");
                options.BaseSnapshotPath = Path.GetFullPath(request.SnapshotPath);
                break;
            case PackBaseMode.RememberedVersion:
                options.UseDiff = true;
                break;
        }

        ApplyPackKeys(options);
        return options;
    }

    private void ApplyPackKeys(PackOptions options)
    {
        if (_mode == TransferKeyMode.PemFolder)
        {
            if (!Paths.HasKeys(_pemFolder, forPacking: true))
                throw new InvalidOperationException("У папці ключів немає файлів для упаковки.");

            options.RecipientPublicKeyPath = Path.Combine(_pemFolder, KeyStore.RecipientPublicFile);
            options.SenderPrivateKeyPath = Path.Combine(_pemFolder, KeyStore.SenderPrivateFile);
            options.KeyPassword = _pemPassword;
            return;
        }

        var key = RequireKey(forPacking: true);
        options.RecipientPublicMaterial = key.RecipientPublic;
        options.SenderPrivateMaterial = key.SenderPrivate;
    }

    private void ApplyUnpackKeys(UnpackOptions options)
    {
        if (_mode == TransferKeyMode.PemFolder)
        {
            if (!Paths.HasKeys(_pemFolder, forPacking: false))
                throw new InvalidOperationException("У папці ключів немає файлів для розпакування.");

            options.RecipientPrivateKeyPath = Path.Combine(_pemFolder, KeyStore.RecipientPrivateFile);
            options.SenderPublicKeyPath = Path.Combine(_pemFolder, KeyStore.SenderPublicFile);
            options.KeyPassword = _pemPassword;
            return;
        }

        var key = RequireKey(forPacking: false);
        options.RecipientPrivateMaterial = key.RecipientPrivate;
        options.SenderPublicMaterial = key.SenderPublic;
    }

    private KeySet RequireKey(bool forPacking)
    {
        if (_key is null)
            throw new InvalidOperationException("Ключ не задано. Відкрийте вкладку «Ключ» і вставте рядок ключа.");

        if (forPacking && !_key.CanPack)
            throw new InvalidOperationException("Цей ключ не містить матеріалу для упаковки.");

        if (!forPacking && !_key.CanUnpack)
            throw new InvalidOperationException("Цей ключ не містить матеріалу для розпакування.");

        return _key;
    }

    private KeyStatus Update(TransferKeyMode mode, KeySet? key, string code, string description, string? source)
    {
        _mode = mode;
        _key = key;
        CurrentCode = code;
        Status = new KeyStatus(key is not null, key?.CanPack ?? false, key?.CanUnpack ?? false, description, source);
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(CurrentCode));
        return Status;
    }

    private static string Describe(KeySet key)
    {
        var abilities = (key.CanPack, key.CanUnpack) switch
        {
            (true, true) => "може пакувати і розпаковувати",
            (true, false) => "може лише пакувати",
            (false, true) => "може лише розпаковувати",
            _ => "не містить потрібного матеріалу"
        };

        return key.Label.Length > 0 ? $"{key.Label}, {abilities}" : abilities;
    }

    private static string DescribePem(bool canPack, bool canUnpack) => (canPack, canUnpack) switch
    {
        (true, true) => "PEM-ключі: можна пакувати і розпаковувати",
        (true, false) => "PEM-ключі: можна лише пакувати",
        (false, true) => "PEM-ключі: можна лише розпаковувати",
        _ => "PEM-ключі неповні"
    };

    private static PackReport ToReport(PackResult result) => new(
        result.IsDelta,
        result.Images
            .Select(i => new TransferImage(i.Path, i.Width, i.Height, i.FileLength, i.PayloadLength))
            .ToList(),
        result.ImageBytes,
        result.SourceBytes,
        result.ArchiveBytes,
        result.CipherBytes,
        result.Summary.FileCount,
        result.Summary.DirectoryCount,
        result.Summary.OriginalBytes,
        result.Summary.TreeDigest.Length > 0 ? TreeDigest.Short(result.Summary.TreeDigest) : string.Empty,
        result.Elapsed,
        result.ReplacedImages,
        result.DeltaStats is null ? null : ToSummary(result.DeltaStats));

    private static DeltaSummary ToSummary(DeltaStats stats) => new(
        stats.KeptCount,
        stats.AddedCount,
        stats.PatchedCount,
        stats.DeletedFileCount,
        stats.DeletedDirectoryCount,
        stats.AddedDirectoryCount,
        stats.LiteralBytes,
        stats.CopiedBytes,
        stats.NewTotalBytes);
}

public sealed class AppSettings
{
    public string LeftPath { get; set; } = string.Empty;
    public string RightPath { get; set; } = string.Empty;
    public string IgnorePatterns { get; set; } = ".git;.vs;bin;obj;node_modules;packages;dist;*.user;*.suo";
    public int WhitespaceMode { get; set; }
    public bool IgnoreLineEndings { get; set; } = true;
    public bool IsSideBySide { get; set; } = true;
    public bool CollapseUnchanged { get; set; }
    public int ContextLines { get; set; } = 3;
    public bool WatchChanges { get; set; }

    public string ImagesFolder { get; set; } = string.Empty;
    public string RestoreFolder { get; set; } = string.Empty;
    public bool PackOnlyChanges { get; set; } = true;
    public int Parts { get; set; }
    public int Threads { get; set; }
    public int CoverBits { get; set; } = 1;
    public List<string> CoverFiles { get; set; } = new();
    public List<string> Presets { get; set; } = new();
    public int StepBits { get; set; } = 4;
    public int CompressionLevel { get; set; } = 11;
    public int ChunkMiB { get; set; }
    public string NamePrefix { get; set; } = "IMG";
    public string ExtraExcludes { get; set; } = string.Empty;
    public string SnapshotPath { get; set; } = string.Empty;
    public double DiffSplitWidth { get; set; }
    public double FileListWidth { get; set; } = 380;
    public bool GroupByFolder { get; set; } = true;
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FolderDiff",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public enum PackBaseMode
{
    Full,
    AgainstFolder,
    SnapshotFile,
    RememberedVersion
}

public enum TransferKeyMode
{
    Code,
    PemFolder
}

public sealed record PackRequest(
    string SourceFolder,
    string OutputFolder,
    PackBaseMode BaseMode,
    string? BaseFolder,
    string? SnapshotPath,
    IReadOnlyList<string> ExcludePatterns,
    int Parts,
    int Threads,
    int StepBits,
    int CompressionLevel,
    int ChunkMiB,
    string NamePrefix,
    bool KeepArchive,
    bool RememberVersion,
    IReadOnlyList<string> Covers,
    int CoverBits,
    string Priority);

public sealed record UnpackRequest(
    string ImagesFolder,
    string TargetFolder,
    bool SkipSignature,
    bool RememberVersion,
    string Priority);

public sealed record SnapshotRequest(
    string SourceFolder,
    string SnapshotPath,
    IReadOnlyList<string> ExcludePatterns);

public sealed class FileItemViewModel
{
    public FileItemViewModel(FileEntry entry) => Entry = entry;

    public FileEntry Entry { get; }

    public string RelativePath => Entry.RelativePath;

    public FileStatus Status => Entry.Status;

    public string Name => Entry.Name;

    public string Folder => Entry.Folder;

    public string StatusGlyph => Status switch
    {
        FileStatus.Added => "+",
        FileStatus.Deleted => "−",
        FileStatus.Modified => "M",
        _ => "="
    };

    public string SizeText => Status switch
    {
        FileStatus.Added => FormatSize(Entry.RightSize),
        FileStatus.Deleted => FormatSize(Entry.LeftSize),
        _ => $"{FormatSize(Entry.LeftSize)} → {FormatSize(Entry.RightSize)}"
    };

    public string Tooltip => $"{RelativePath}{Environment.NewLine}{SizeText}{(Entry.IsBinary ? Environment.NewLine + "бінарний" : string.Empty)}";

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes / (1024.0 * 1024.0):0.##} МБ"
    };
}

public enum FileNodeKind
{
    Header,
    Root,
    Folder,
    File
}

public partial class FileTreeNode : ObservableObject
{
    private FileTreeNode(string name, FileNodeKind kind, FileItemViewModel? file = null)
    {
        Name = name;
        Kind = kind;
        File = file;
    }

    public string Name { get; private set; }

    public FileNodeKind Kind { get; }

    public FileItemViewModel? File { get; }

    public ObservableCollection<FileTreeNode> Children { get; } = new();

    public bool IsFile => Kind == FileNodeKind.File;

    public string StatusGlyph => File?.StatusGlyph ?? string.Empty;

    public FileStatus Status => File?.Status ?? FileStatus.Unchanged;

    public string Tooltip => File?.Tooltip ?? Name;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected;

    public static ObservableCollection<FileTreeNode> Build(
        IReadOnlyList<FileItemViewModel> files,
        string rootPath)
    {
        var folderRoot = new FileTreeNode(string.Empty, FileNodeKind.Folder);
        var folders = new Dictionary<string, FileTreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var segments = file.RelativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            var parent = folderRoot;
            var prefix = string.Empty;

            for (var i = 0; i < segments.Length - 1; i++)
            {
                prefix = prefix.Length == 0 ? segments[i] : $"{prefix}/{segments[i]}";

                if (!folders.TryGetValue(prefix, out var folder))
                {
                    folder = new FileTreeNode(segments[i], FileNodeKind.Folder);
                    folders[prefix] = folder;
                    parent.Children.Add(folder);
                }

                parent = folder;
            }

            parent.Children.Add(new FileTreeNode(segments[^1], FileNodeKind.File, file));
        }

        CollapseChains(folderRoot);
        Sort(folderRoot);

        var header = new FileTreeNode($"Зміни ({files.Count})", FileNodeKind.Header);

        if (files.Count == 0)
            return new ObservableCollection<FileTreeNode> { header };

        var root = new FileTreeNode(
            string.IsNullOrWhiteSpace(rootPath) ? "." : rootPath.TrimEnd(Path.DirectorySeparatorChar),
            FileNodeKind.Root);

        foreach (var child in folderRoot.Children)
            root.Children.Add(child);

        header.Children.Add(root);
        return new ObservableCollection<FileTreeNode> { header };
    }

    public IEnumerable<FileTreeNode> Flatten()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var node in child.Flatten())
                yield return node;
        }
    }

    private static void CollapseChains(FileTreeNode node)
    {
        foreach (var child in node.Children)
            CollapseChains(child);

        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            while (child.Kind == FileNodeKind.Folder &&
                   child.Children.Count == 1 &&
                   child.Children[0].Kind == FileNodeKind.Folder)
            {
                var only = child.Children[0];
                var merged = new FileTreeNode($"{child.Name}\\{only.Name}", FileNodeKind.Folder);

                foreach (var grandChild in only.Children)
                    merged.Children.Add(grandChild);

                node.Children[i] = merged;
                child = merged;
            }
        }
    }

    private static void Sort(FileTreeNode node)
    {
        var ordered = node.Children
            .OrderByDescending(c => c.Kind == FileNodeKind.Folder)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        node.Children.Clear();
        foreach (var child in ordered)
        {
            node.Children.Add(child);
            Sort(child);
        }
    }
}

public interface IComparePaths : INotifyPropertyChanged
{
    string LeftPath { get; }

    string RightPath { get; }

    IReadOnlyList<string> ExcludePatterns { get; }
}

public partial class KeysViewModel : ObservableObject
{
    private readonly ImageTransferService _transfer;
    private readonly IDialogService _dialogs;

    public KeysViewModel(ImageTransferService transfer, IDialogService dialogs)
    {
        _transfer = transfer;
        _dialogs = dialogs;
        _transfer.PropertyChanged += OnTransferChanged;
    }

    public KeyStatus Status => _transfer.Status;

    public string KeyFilePath => _transfer.KeyFilePath;

    public bool KeyFileExists => _transfer.KeyFileExists;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCodeCommand))]
    private string _code = string.Empty;

    [ObservableProperty]
    private string _machineA = "machine-a";

    [ObservableProperty]
    private string _machineB = "machine-b";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyGeneratedACommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyGeneratedBCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseGeneratedACommand))]
    [NotifyCanExecuteChangedFor(nameof(SavePairCommand))]
    private string _generatedA = string.Empty;

    [ObservableProperty]
    private string _generatedB = string.Empty;

    [ObservableProperty]
    private string _pemFolder = string.Empty;

    [ObservableProperty]
    private string _pemPassword = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    public void ApplySettings(AppSettings settings)
    {
        _ = settings;
        Load();
    }

    public void WriteSettings(AppSettings settings) => _ = settings;

    public void Load()
    {
        var fromFile = _transfer.TryAutoLoad();
        if (fromFile.IsValid)
        {
            Code = _transfer.CurrentCode;
            Message = $"Ключ узято з файлу: {fromFile.Source}";
            return;
        }

        Message = $"Ключа немає. Покладіть файл {Path.GetFileName(KeyFilePath)} поруч з програмою або вставте рядок нижче.";
    }

    private bool CanApplyCode() => !string.IsNullOrWhiteSpace(Code);

    private bool HasGenerated() => GeneratedA.Length > 0 && GeneratedB.Length > 0;

    private bool CanUsePem() => !string.IsNullOrWhiteSpace(PemFolder);

    [RelayCommand]
    private void InstallKeyFile()
    {
        var picked = _dialogs.PickFile("Файл ключа imgkeys.txt", "Ключ (*.txt)|*.txt|Усі файли (*.*)|*.*");
        if (picked is null)
            return;

        var status = _transfer.InstallKeyFile(picked);
        if (status.IsValid)
        {
            Code = _transfer.CurrentCode;
            Message = $"Файл покладено поруч з програмою. {status.Description}";
        }
        else
        {
            Message = status.Description;
        }
    }

    [RelayCommand]
    private void ReloadFromFile() => Load();

    [RelayCommand]
    private void OpenKeyFolder()
    {
        var folder = Path.GetDirectoryName(KeyFilePath);
        if (folder is not null && Directory.Exists(folder))
            _dialogs.OpenInShell(folder);
    }

    [RelayCommand(CanExecute = nameof(CanApplyCode))]
    private void ApplyCode()
    {
        var status = _transfer.SetKey(Code.Trim());
        Message = status.IsValid
            ? $"Ключ прийнято. {status.Description}"
            : $"Ключ не прийнято. {status.Description}";
    }

    [RelayCommand(CanExecute = nameof(CanApplyCode))]
    private void CopyCode() => _dialogs.CopyToClipboard(Code);

    [RelayCommand]
    private void SaveCodeAsFile()
    {
        if (string.IsNullOrWhiteSpace(Code))
            return;

        try
        {
            File.WriteAllText(KeyFilePath, Code.Trim());
            Message = $"Записано у {KeyFilePath}";
            OnPropertyChanged(nameof(KeyFileExists));
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private void CreatePair()
    {
        try
        {
            var (codeA, codeB) = _transfer.CreatePair(MachineA.Trim(), MachineB.Trim());
            GeneratedA = codeA;
            GeneratedB = codeB;
            Message = "Пару створено. Один ключ лишається цій машині, другий передайте на другу машину.";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasGenerated))]
    private void CopyGeneratedA() => _dialogs.CopyToClipboard(GeneratedA);

    [RelayCommand(CanExecute = nameof(HasGenerated))]
    private void CopyGeneratedB() => _dialogs.CopyToClipboard(GeneratedB);

    [RelayCommand(CanExecute = nameof(HasGenerated))]
    private void UseGeneratedA()
    {
        Code = GeneratedA;
        ApplyCode();
    }

    [RelayCommand(CanExecute = nameof(HasGenerated))]
    private void SavePair()
    {
        var path = _dialogs.PickSaveFile("Зберегти пару ключів", "img-key-pair.txt");
        if (path is null)
            return;

        try
        {
            var text = string.Join(Environment.NewLine, new[]
            {
                $"# {MachineA}", GeneratedA, string.Empty, $"# {MachineB}", GeneratedB, string.Empty
            });
            File.WriteAllText(path, text);
            Message = $"Збережено у {path}";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private void BrowsePemFolder()
    {
        var picked = _dialogs.PickFolder("Папка з PEM-ключами", PemFolder);
        if (picked is not null)
        {
            PemFolder = picked;
            UsePem();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUsePem))]
    private void UsePem()
    {
        var status = _transfer.UsePemFolder(PemFolder, PemPassword);
        Message = status.IsValid ? status.Description : $"PEM-ключі не підійшли. {status.Description}";
    }

    [RelayCommand]
    private void GeneratePem()
    {
        var folder = _dialogs.PickFolder("Куди створити PEM-ключі", PemFolder);
        if (folder is null)
            return;

        try
        {
            _transfer.GeneratePemKeysAsync(folder, PemPassword).GetAwaiter().GetResult();
            PemFolder = folder;
            Message = $"Створено чотири PEM-файли у {folder}";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    private void OnTransferChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(KeyFileExists));
    }
}

public partial class MainViewModel : ObservableObject, IComparePaths, IDisposable
{
    private const long MaxDiffBytes = 20L * 1024 * 1024;

    private readonly FolderComparer _comparer = new();
    private readonly DiffService _diffService = new();
    private readonly FolderWatcher _watcher = new();
    private readonly IDialogService _dialogs;
    private readonly SettingsService _settingsService;

    private List<FileItemViewModel> _allFiles = new();
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<int> _changeStarts = Array.Empty<int>();
    private int _currentChange = -1;
    private int _diffVersion;
    private string _selectedLeftText = string.Empty;
    private string _selectedRightText = string.Empty;

    public MainViewModel(IDialogService dialogs, SettingsService settingsService, ImageTransferService transfer)
    {
        _dialogs = dialogs;
        _settingsService = settingsService;
        StatusFilters = StatusFilterOption.CreateDefaults();
        _selectedStatusFilter = StatusFilters[0];
        WhitespaceOptions = WhitespaceOption.CreateDefaults();
        _selectedWhitespace = WhitespaceOptions[0];

        var settings = settingsService.Load();
        _leftPath = settings.LeftPath;
        _rightPath = settings.RightPath;
        _ignorePatterns = settings.IgnorePatterns;
        _selectedWhitespace = WhitespaceOptions.FirstOrDefault(o => (int)o.Mode == settings.WhitespaceMode)
                              ?? WhitespaceOptions[0];
        _groupByFolder = settings.GroupByFolder;
        _ignoreLineEndings = settings.IgnoreLineEndings;
        _isSideBySide = settings.IsSideBySide;
        _collapseUnchanged = settings.CollapseUnchanged;
        _contextLines = ContextLineOptions.Contains(settings.ContextLines) ? settings.ContextLines : 3;
        _watchChanges = settings.WatchChanges;
        _diffSplitWidth = settings.DiffSplitWidth;
        _fileListWidth = settings.FileListWidth > 0 ? settings.FileListWidth : 380;

        _watcher.Changed += OnWatchedFolderChanged;

        Transfer = new TransferViewModel(transfer, dialogs, this);
        Keys = new KeysViewModel(transfer, dialogs);
        Transfer.ApplySettings(settings);
        Keys.ApplySettings(settings);
    }

    public TransferViewModel Transfer { get; private set; } = null!;

    public KeysViewModel Keys { get; private set; } = null!;

    public IReadOnlyList<string> ExcludePatterns
    {
        get
        {
            var result = new List<string>();

            foreach (var pattern in ParsePatterns(IgnorePatterns))
            {
                result.Add(pattern);
                if (!pattern.Contains('/') && !pattern.Contains('\\') &&
                    !pattern.Contains('*') && !pattern.Contains('?'))
                    result.Add(pattern + "/");
            }

            return result;
        }
    }

    public void PersistSettings()
    {
        var settings = new AppSettings
        {
            LeftPath = LeftPath,
            RightPath = RightPath,
            IgnorePatterns = IgnorePatterns,
            WhitespaceMode = (int)Whitespace,
            GroupByFolder = GroupByFolder,
            IgnoreLineEndings = IgnoreLineEndings,
            IsSideBySide = IsSideBySide,
            CollapseUnchanged = CollapseUnchanged,
            ContextLines = ContextLines,
            WatchChanges = WatchChanges,
            DiffSplitWidth = DiffSplitWidth,
            FileListWidth = FileListWidth
        };

        Transfer.WriteSettings(settings);
        Keys.WriteSettings(settings);
        _settingsService.Save(settings);
    }

    public void Dispose()
    {
        _watcher.Changed -= OnWatchedFolderChanged;
        _watcher.Dispose();
        _cancellation?.Dispose();
    }

    public IReadOnlyList<StatusFilterOption> StatusFilters { get; }

    public IReadOnlyList<WhitespaceOption> WhitespaceOptions { get; }

    public IReadOnlyList<int> ContextLineOptions { get; } = new[] { 1, 3, 5, 10, 25 };

    public WhitespaceMode Whitespace => SelectedWhitespace.Mode;

    public string WhitespaceHint => SelectedWhitespace.Hint;

    public ObservableCollection<FileTreeNode> FileTree { get; private set; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private string _leftPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private string _rightPath = string.Empty;

    [ObservableProperty]
    private string _ignorePatterns = ".git;.vs;bin;obj;node_modules;packages;dist;*.user;*.suo";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhitespaceHint))]
    private WhitespaceOption _selectedWhitespace;

    [ObservableProperty]
    private bool _groupByFolder = true;

    [ObservableProperty]
    private bool _ignoreLineEndings = true;

    [ObservableProperty]
    private bool _isSideBySide = true;

    [ObservableProperty]
    private bool _collapseUnchanged;

    [ObservableProperty]
    private int _contextLines = 3;

    [ObservableProperty]
    private double _diffSplitWidth;

    [ObservableProperty]
    private double _fileListWidth = 380;

    [ObservableProperty]
    private bool _watchChanges;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private StatusFilterOption _selectedStatusFilter;

    [ObservableProperty]
    private IReadOnlyList<FileItemViewModel> _visibleFiles = Array.Empty<FileItemViewModel>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevealSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyPathCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyDiffCommand))]
    private FileItemViewModel? _selectedFile;

    [ObservableProperty]
    private IReadOnlyList<DiffRow> _diffRows = Array.Empty<DiffRow>();

    [ObservableProperty]
    private int _scrollToRow = -1;

    [ObservableProperty]
    private string _diffHeader = "Виберіть файл зі списку";

    [ObservableProperty]
    private string _diffStats = string.Empty;

    [ObservableProperty]
    private string _leftPaneTitle = "До";

    [ObservableProperty]
    private string _rightPaneTitle = "Після";

    [ObservableProperty]
    private string _statusText = "Вкажіть дві папки та натисніть «Порівняти»";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _hasResult;

    [ObservableProperty]
    private int _addedCount;

    [ObservableProperty]
    private int _deletedCount;

    [ObservableProperty]
    private int _modifiedCount;

    [ObservableProperty]
    private int _unchangedCount;

    private bool CanCompare() =>
        !IsBusy && !string.IsNullOrWhiteSpace(LeftPath) && !string.IsNullOrWhiteSpace(RightPath);

    private bool CanRefresh() => CanCompare() && HasResult;

    private bool CanCancel() => IsBusy;

    private bool HasSelection() => SelectedFile is not null;

    [RelayCommand]
    private void BrowseLeft()
    {
        var picked = _dialogs.PickFolder("Ліва папка (версія «до»)", LeftPath);
        if (picked is not null)
            LeftPath = picked;
    }

    [RelayCommand]
    private void BrowseRight()
    {
        var picked = _dialogs.PickFolder("Права папка (версія «після»)", RightPath);
        if (picked is not null)
            RightPath = picked;
    }

    [RelayCommand]
    private void Swap() => (LeftPath, RightPath) = (RightPath, LeftPath);

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private Task Compare() => RunCompareAsync(preserveSelection: false);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task Refresh() => RunCompareAsync(preserveSelection: true);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenSelected()
    {
        var path = SelectedFile?.Entry.RightFullPath ?? SelectedFile?.Entry.LeftFullPath;
        if (path is not null)
            _dialogs.OpenInShell(path);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RevealSelected()
    {
        var path = SelectedFile?.Entry.RightFullPath ?? SelectedFile?.Entry.LeftFullPath;
        if (path is not null)
            _dialogs.RevealInExplorer(path);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPath()
    {
        var path = SelectedFile?.Entry.RightFullPath ?? SelectedFile?.Entry.LeftFullPath;
        if (path is not null)
            _dialogs.CopyToClipboard(path);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyDiff()
    {
        if (SelectedFile?.Entry.IsBinary != false)
            return;

        var text = _diffService.BuildUnifiedText(_selectedLeftText, _selectedRightText, Whitespace);
        _dialogs.CopyToClipboard(text);
    }

    [RelayCommand]
    private void CopyRow(DiffRow? row)
    {
        if (row is null)
            return;

        var text = row.IsSeparator
            ? row.SeparatorText
            : IsSideBySide
                ? $"{row.LeftText}{Environment.NewLine}{row.RightText}"
                : row.LeftText;

        _dialogs.CopyToClipboard(text);
    }

    [RelayCommand]
    private void NextChange() => MoveToChange(1);

    [RelayCommand]
    private void PreviousChange() => MoveToChange(-1);

    private void MoveToChange(int direction)
    {
        if (_changeStarts.Count == 0)
            return;

        _currentChange = _currentChange < 0
            ? (direction > 0 ? 0 : _changeStarts.Count - 1)
            : Math.Clamp(_currentChange + direction, 0, _changeStarts.Count - 1);

        ScrollToRow = -1;
        ScrollToRow = _changeStarts[_currentChange];
        StatusText = $"Зміна {_currentChange + 1} з {_changeStarts.Count}";
    }

    private async Task RunCompareAsync(bool preserveSelection)
    {
        if (!Directory.Exists(LeftPath))
        {
            _dialogs.ShowError($"Ліва папка не знайдена:{Environment.NewLine}{LeftPath}");
            return;
        }

        if (!Directory.Exists(RightPath))
        {
            _dialogs.ShowError($"Права папка не знайдена:{Environment.NewLine}{RightPath}");
            return;
        }

        var previousPath = preserveSelection ? SelectedFile?.RelativePath : null;

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        StatusText = "Сканування...";
        SelectedFile = null;
        DiffRows = Array.Empty<DiffRow>();
        _allFiles = new List<FileItemViewModel>();
        VisibleFiles = Array.Empty<FileItemViewModel>();

        var options = new CompareOptions
        {
            IgnorePatterns = ParsePatterns(IgnorePatterns),
            Whitespace = Whitespace,
            IgnoreLineEndings = IgnoreLineEndings
        };

        var progress = new Progress<CompareProgress>(p =>
        {
            StatusText = p.Message;
            if (p.Total > 0)
            {
                IsProgressIndeterminate = false;
                ProgressValue = 100.0 * p.Done / p.Total;
            }
        });

        try
        {
            var result = await _comparer.CompareAsync(LeftPath, RightPath, options, progress, token);

            _allFiles = result.Entries.Select(e => new FileItemViewModel(e)).ToList();
            AddedCount = result.AddedCount;
            DeletedCount = result.DeletedCount;
            ModifiedCount = result.ModifiedCount;
            UnchangedCount = result.UnchangedCount;
            LeftPaneTitle = $"До  ·  {result.LeftRoot}";
            RightPaneTitle = $"Після  ·  {result.RightRoot}";
            HasResult = true;
            ApplyFilter();

            SyncTreeSelection();
            SelectedFile = previousPath is null
                ? FirstInteresting()
                : VisibleFiles.FirstOrDefault(f =>
                      string.Equals(f.RelativePath, previousPath, StringComparison.OrdinalIgnoreCase))
                  ?? FirstInteresting();

            var changed = AddedCount + DeletedCount + ModifiedCount;
            StatusText = changed == 0
                ? $"Різниць немає. Файлів: {result.Entries.Count}. {result.Elapsed.TotalSeconds:0.00} с"
                : $"Змін: {changed} з {result.Entries.Count} файлів. {result.Elapsed.TotalSeconds:0.00} с";

            if (result.Errors.Count > 0)
                StatusText += $" | пропущено з помилками: {result.Errors.Count}";

            UpdateWatcher();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Скасовано";
        }
        catch (Exception ex)
        {
            StatusText = "Помилка порівняння";
            _dialogs.ShowError(ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
        }
    }

    partial void OnWatchChangesChanged(bool value) => UpdateWatcher();

    private void UpdateWatcher()
    {
        if (WatchChanges && HasResult)
            _watcher.Start(new[] { LeftPath, RightPath });
        else
            _watcher.Stop();
    }

    private void OnWatchedFolderChanged(object? sender, EventArgs e)
    {
        if (!WatchChanges || IsBusy || !HasResult)
            return;

        StatusText = "Виявлено зміни у папках, оновлюю...";
        _ = RunCompareAsync(preserveSelection: true);
    }

    partial void OnIgnorePatternsChanged(string value) => OnPropertyChanged(nameof(ExcludePatterns));

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedStatusFilterChanged(StatusFilterOption value) => ApplyFilter();

    partial void OnDiffRowsChanged(IReadOnlyList<DiffRow> value)
    {
        if (_changeStarts.Count == 0)
        {
            _currentChange = -1;
            ScrollToRow = 0;
            return;
        }

        _currentChange = 0;
        ScrollToRow = _changeStarts[0];
    }

    partial void OnIsSideBySideChanged(bool value) => _ = LoadDiffAsync(SelectedFile);

    partial void OnCollapseUnchangedChanged(bool value) => _ = LoadDiffAsync(SelectedFile);

    partial void OnContextLinesChanged(int value) => _ = LoadDiffAsync(SelectedFile);

    partial void OnSelectedWhitespaceChanged(WhitespaceOption value) => RecompareOrReload();

    partial void OnGroupByFolderChanged(bool value) => ApplyFilter();

    partial void OnIgnoreLineEndingsChanged(bool value) => RecompareOrReload();

    private void RecompareOrReload()
    {
        if (HasResult && !IsBusy)
            _ = RunCompareAsync(preserveSelection: true);
        else
            _ = LoadDiffAsync(SelectedFile);
    }

    partial void OnSelectedFileChanged(FileItemViewModel? value) => _ = LoadDiffAsync(value);

    private FileItemViewModel? FirstInteresting() =>
        VisibleFiles.FirstOrDefault(f => !f.Entry.IsBinary && f.Status == FileStatus.Modified)
        ?? VisibleFiles.FirstOrDefault(f => !f.Entry.IsBinary)
        ?? VisibleFiles.FirstOrDefault();

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        var filter = SelectedStatusFilter;

        var filtered = _allFiles
            .Where(f => filter.Match(f.Status))
            .Where(f => search.Length == 0 ||
                        f.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        VisibleFiles = filtered;
        FileTree = FileTreeNode.Build(filtered, RightPath);
        OnPropertyChanged(nameof(FileTree));
        SyncTreeSelection();

        if (SelectedFile is not null && !filtered.Contains(SelectedFile))
            SelectedFile = null;
    }

    private void SyncTreeSelection()
    {
        if (SelectedFile is null)
            return;

        foreach (var node in FileTree.SelectMany(root => root.Flatten()))
        {
            if (ReferenceEquals(node.File, SelectedFile))
            {
                node.IsSelected = true;
                return;
            }
        }
    }

    private async Task LoadDiffAsync(FileItemViewModel? item)
    {
        var version = ++_diffVersion;
        _changeStarts = Array.Empty<int>();
        _currentChange = -1;
        ScrollToRow = -1;
        DiffStats = string.Empty;
        _selectedLeftText = string.Empty;
        _selectedRightText = string.Empty;

        if (item is null)
        {
            DiffRows = Array.Empty<DiffRow>();
            DiffHeader = "Виберіть файл зі списку";
            return;
        }

        DiffHeader = $"{item.RelativePath}    {item.SizeText}";

        if (item.Entry.IsBinary)
        {
            DiffRows = _diffService.BuildMessage("Бінарний файл. Порівняння рядків недоступне.").Rows;
            return;
        }

        var biggest = Math.Max(item.Entry.LeftSize, item.Entry.RightSize);
        if (biggest > MaxDiffBytes)
        {
            DiffRows = _diffService.BuildMessage(
                $"Файл завеликий для порядкового порівняння: {FileItemViewModel.FormatSize(biggest)}. Ліміт 20 МБ.").Rows;
            return;
        }

        var entry = item.Entry;
        var sideBySide = IsSideBySide;
        var viewOptions = new DiffViewOptions(Whitespace, CollapseUnchanged, ContextLines);

        try
        {
            var loaded = await Task.Run(() =>
            {
                var leftText = entry.LeftFullPath is null
                    ? string.Empty
                    : FileContentService.NormalizeLineEndings(FileContentService.ReadText(entry.LeftFullPath));
                var rightText = entry.RightFullPath is null
                    ? string.Empty
                    : FileContentService.NormalizeLineEndings(FileContentService.ReadText(entry.RightFullPath));

                var document = sideBySide
                    ? _diffService.BuildSideBySide(leftText, rightText, viewOptions)
                    : _diffService.BuildInline(leftText, rightText, viewOptions);

                return (leftText, rightText, document);
            });

            if (version != _diffVersion)
                return;

            _selectedLeftText = loaded.leftText;
            _selectedRightText = loaded.rightText;
            _changeStarts = loaded.document.ChangeStarts;
            DiffRows = loaded.document.Rows;
            DiffStats = $"+{loaded.document.AddedLines}  −{loaded.document.DeletedLines}";

            if (!loaded.document.HasVisibleDifferences && entry.Status == FileStatus.Modified)
                DiffStats = "відрізняються лише невидимі символи або кінці рядків";
        }
        catch (Exception ex)
        {
            if (version != _diffVersion)
                return;

            DiffRows = _diffService.BuildMessage($"Не вдалося прочитати файл: {ex.Message}").Rows;
        }
    }

    private static IReadOnlyList<string> ParsePatterns(string raw) =>
        raw.Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public partial class PresetOption : ObservableObject
{
    public PresetOption(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;
}

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

public partial class TransferViewModel : ObservableObject
{
    private const int MaxLogLines = 4000;

    private readonly ImageTransferService _transfer;
    private readonly IDialogService _dialogs;
    private readonly IComparePaths _paths;

    public TransferViewModel(ImageTransferService transfer, IDialogService dialogs, IComparePaths paths)
    {
        _transfer = transfer;
        _dialogs = dialogs;
        _paths = paths;

        foreach (var name in _transfer.PresetNames)
        {
            var option = new PresetOption(name);
            option.PropertyChanged += (_, _) => OnPropertyChanged(nameof(EffectiveExcludesText));
            Presets.Add(option);
        }

        _paths.PropertyChanged += OnPathsChanged;
        _transfer.PropertyChanged += OnTransferChanged;
    }

    public ObservableCollection<string> Log { get; } = new();

    public ObservableCollection<string> CoverFiles { get; } = new();

    public ObservableCollection<PresetOption> Presets { get; } = new();

    public IReadOnlyList<int> PartsOptions { get; } = new[] { 0, 1, 2, 3, 5, 8, 10, 16 };

    public IReadOnlyList<int> ThreadsOptions { get; } = new[] { 0, 2, 4, 6, 8, 12, 16 };

    public IReadOnlyList<int> StepBitsOptions { get; } = new[] { 2, 3, 4, 5, 6, 8 };

    public IReadOnlyList<int> LevelOptions { get; } = new[] { 1, 3, 5, 7, 9, 11 };

    public IReadOnlyList<int> ChunkOptions { get; } = new[] { 0, 8, 16, 32, 64 };

    public IReadOnlyList<int> CoverBitsOptions { get; } = new[] { 1, 2, 3, 4 };

    public IReadOnlyList<string> PriorityOptions => _transfer.PriorityOptions;

    public string SourceFolder => _paths.RightPath;

    public string BaseFolder => _paths.LeftPath;

    public bool CanPackWithKey => _transfer.Status.CanPack;

    public bool CanUnpackWithKey => _transfer.Status.CanUnpack;

    public string KeySummary => _transfer.Status.IsValid
        ? _transfer.Status.Description
        : $"{_transfer.Status.Description}. Відкрийте вкладку «Ключ».";

    public string EffectiveExcludesText
    {
        get
        {
            var list = EffectiveExcludes();
            return list.Count == 0 ? "нічого не виключено" : string.Join("   ", list);
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PackCommand))]
    [NotifyCanExecuteChangedFor(nameof(DryRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputFolderCommand))]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnpackCommand))]
    private string _imagesFolder = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnpackCommand))]
    private string _restoreFolder = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PackCommand))]
    [NotifyCanExecuteChangedFor(nameof(DryRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(EstimateCommand))]
    [NotifyPropertyChangedFor(nameof(BaseDescription))]
    private PackBaseMode _baseMode = PackBaseMode.AgainstFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PackCommand))]
    [NotifyCanExecuteChangedFor(nameof(EstimateCommand))]
    [NotifyPropertyChangedFor(nameof(BaseDescription))]
    private string _snapshotPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveExcludesText))]
    private string _extraExcludes = string.Empty;

    [ObservableProperty]
    private int _parts;

    [ObservableProperty]
    private int _threads;

    [ObservableProperty]
    private int _stepBits = 4;

    [ObservableProperty]
    private int _compressionLevel = 11;

    [ObservableProperty]
    private int _chunkMiB;

    [ObservableProperty]
    private string _namePrefix = "IMG";

    [ObservableProperty]
    private bool _keepArchive;

    [ObservableProperty]
    private bool _rememberVersion;

    [ObservableProperty]
    private int _coverBits = 1;

    [ObservableProperty]
    private bool _skipSignature;

    [ObservableProperty]
    private bool _rememberAfterUnpack;

    [ObservableProperty]
    private string _priority = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PackCommand))]
    [NotifyCanExecuteChangedFor(nameof(DryRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnpackCommand))]
    [NotifyCanExecuteChangedFor(nameof(EstimateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateSnapshotCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _operationTitle = string.Empty;

    [ObservableProperty]
    private PackReport? _packReport;

    [ObservableProperty]
    private UnpackReport? _unpackReport;

    [ObservableProperty]
    private DeltaSummary? _deltaEstimate;

    [ObservableProperty]
    private string _errorText = string.Empty;

    public string BaseDescription => BaseMode switch
    {
        PackBaseMode.Full => "Поїде вся папка, завжди 5 картинок.",
        PackBaseMode.AgainstFolder => string.IsNullOrWhiteSpace(BaseFolder)
            ? "Вкажіть ліву папку «До» угорі вікна."
            : $"База: {BaseFolder}",
        PackBaseMode.SnapshotFile => string.IsNullOrWhiteSpace(SnapshotPath)
            ? "Виберіть файл знімка .icv."
            : $"Знімок: {SnapshotPath}",
        _ => "Порівняння з версією, запамʼятованою в самій папці."
    };

    public void ApplySettings(AppSettings settings)
    {
        OutputFolder = settings.ImagesFolder;
        ImagesFolder = settings.ImagesFolder;
        RestoreFolder = settings.RestoreFolder;
        BaseMode = settings.PackOnlyChanges ? PackBaseMode.AgainstFolder : PackBaseMode.Full;
        Parts = PartsOptions.Contains(settings.Parts) ? settings.Parts : 0;
        Threads = ThreadsOptions.Contains(settings.Threads) ? settings.Threads : 0;
        CoverBits = CoverBitsOptions.Contains(settings.CoverBits) ? settings.CoverBits : 1;
        StepBits = StepBitsOptions.Contains(settings.StepBits) ? settings.StepBits : 4;
        CompressionLevel = LevelOptions.Contains(settings.CompressionLevel) ? settings.CompressionLevel : 11;
        ChunkMiB = ChunkOptions.Contains(settings.ChunkMiB) ? settings.ChunkMiB : 0;
        NamePrefix = string.IsNullOrWhiteSpace(settings.NamePrefix) ? "IMG" : settings.NamePrefix;
        ExtraExcludes = settings.ExtraExcludes;
        SnapshotPath = settings.SnapshotPath;

        CoverFiles.Clear();
        foreach (var file in settings.CoverFiles)
            CoverFiles.Add(file);

        foreach (var preset in Presets)
            preset.IsSelected = settings.Presets.Contains(preset.Name, StringComparer.OrdinalIgnoreCase);
    }

    public void WriteSettings(AppSettings settings)
    {
        settings.ImagesFolder = OutputFolder;
        settings.RestoreFolder = RestoreFolder;
        settings.PackOnlyChanges = BaseMode != PackBaseMode.Full;
        settings.Parts = Parts;
        settings.Threads = Threads;
        settings.CoverBits = CoverBits;
        settings.StepBits = StepBits;
        settings.CompressionLevel = CompressionLevel;
        settings.ChunkMiB = ChunkMiB;
        settings.NamePrefix = NamePrefix;
        settings.ExtraExcludes = ExtraExcludes;
        settings.SnapshotPath = SnapshotPath;
        settings.CoverFiles = CoverFiles.ToList();
        settings.Presets = Presets.Where(p => p.IsSelected).Select(p => p.Name).ToList();
    }

    private bool CanPack() =>
        !IsRunning && CanPackWithKey &&
        !string.IsNullOrWhiteSpace(SourceFolder) &&
        !string.IsNullOrWhiteSpace(OutputFolder) &&
        BaseIsReady();

    private bool CanUnpack() =>
        !IsRunning && CanUnpackWithKey &&
        !string.IsNullOrWhiteSpace(ImagesFolder) &&
        !string.IsNullOrWhiteSpace(RestoreFolder);

    private bool CanEstimate() =>
        !IsRunning &&
        !string.IsNullOrWhiteSpace(SourceFolder) &&
        BaseMode switch
        {
            PackBaseMode.AgainstFolder => !string.IsNullOrWhiteSpace(BaseFolder),
            PackBaseMode.SnapshotFile => !string.IsNullOrWhiteSpace(SnapshotPath),
            _ => false
        };

    private bool CanCreateSnapshot() => !IsRunning && !string.IsNullOrWhiteSpace(SourceFolder);

    private bool CanOpenOutput() => Directory.Exists(OutputFolder);

    private bool BaseIsReady() => BaseMode switch
    {
        PackBaseMode.AgainstFolder => !string.IsNullOrWhiteSpace(BaseFolder),
        PackBaseMode.SnapshotFile => !string.IsNullOrWhiteSpace(SnapshotPath),
        _ => true
    };

    [RelayCommand]
    private void BrowseOutput()
    {
        var picked = _dialogs.PickFolder("Куди покласти картинки", OutputFolder);
        if (picked is not null)
            OutputFolder = picked;
    }

    [RelayCommand]
    private void BrowseImages()
    {
        var picked = _dialogs.PickFolder("Папка з картинками", ImagesFolder);
        if (picked is not null)
            ImagesFolder = picked;
    }

    [RelayCommand]
    private void BrowseRestore()
    {
        var picked = _dialogs.PickFolder("Куди відновити папку", RestoreFolder);
        if (picked is not null)
            RestoreFolder = picked;
    }

    [RelayCommand]
    private void BrowseSnapshot()
    {
        var picked = _dialogs.PickFile("Файл знімка", "Знімок (*.icv)|*.icv|Усі файли (*.*)|*.*");
        if (picked is not null)
            SnapshotPath = picked;
    }

    [RelayCommand]
    private void UseRightAsRestore() => RestoreFolder = _paths.RightPath;

    [RelayCommand]
    private void UseOutputAsImages() => ImagesFolder = OutputFolder;

    [RelayCommand]
    private void AddCover()
    {
        var picked = _dialogs.PickPngFiles("Фото-обкладинки PNG");
        if (picked is null)
            return;

        foreach (var file in picked)
        {
            if (!CoverFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                CoverFiles.Add(file);
        }
    }

    [RelayCommand]
    private void ClearCovers() => CoverFiles.Clear();

    [RelayCommand]
    private void LoadExcludeFile()
    {
        var picked = _dialogs.PickFile(
            "Файл із шаблонами",
            "Текст (*.txt;*.gitignore)|*.txt;*.gitignore|Усі файли (*.*)|*.*");
        if (picked is null)
            return;

        try
        {
            var patterns = _transfer.ReadPatternFile(picked);
            var merged = ParseExtra().Concat(patterns).Distinct(StringComparer.OrdinalIgnoreCase);
            ExtraExcludes = string.Join(";", merged);
            Append($"Додано шаблонів з файлу: {patterns.Count}");
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    [RelayCommand]
    private void CopyLog() => _dialogs.CopyToClipboard(string.Join(Environment.NewLine, Log));

    [RelayCommand(CanExecute = nameof(CanOpenOutput))]
    private void OpenOutputFolder() => _dialogs.OpenInShell(OutputFolder);

    [RelayCommand(CanExecute = nameof(CanCreateSnapshot))]
    private Task CreateSnapshot()
    {
        var suggested = $"{new DirectoryInfo(SourceFolder).Name}-base.icv";
        var path = _dialogs.PickSaveFile("Зберегти знімок папки", suggested);
        if (path is null)
            return Task.CompletedTask;

        return RunAsync("Знімок папки", async progress =>
        {
            var written = await _transfer.WriteSnapshotAsync(
                new SnapshotRequest(SourceFolder, path, EffectiveExcludes()), progress);
            SnapshotPath = written;
            PackReport = null;
            UnpackReport = null;
        });
    }

    [RelayCommand(CanExecute = nameof(CanEstimate))]
    private Task Estimate() => RunAsync("Оцінка різниці", async progress =>
    {
        var summary = BaseMode == PackBaseMode.SnapshotFile
            ? await _transfer.EstimateAgainstSnapshotAsync(SnapshotPath, SourceFolder, EffectiveExcludes(), progress)
            : await _transfer.EstimateDeltaAsync(BaseFolder, SourceFolder, EffectiveExcludes(), progress);

        DeltaEstimate = summary;
        PackReport = null;
        UnpackReport = null;
    });

    [RelayCommand(CanExecute = nameof(CanPack))]
    private Task DryRun() => RunAsync("Пробний прогін", async progress =>
    {
        var report = await _transfer.PreviewAsync(BuildPackRequest(), progress);
        PackReport = report;
        UnpackReport = null;
        DeltaEstimate = report.Delta;
    });

    [RelayCommand(CanExecute = nameof(CanPack))]
    private Task Pack() => RunAsync("Упаковка в картинки", async progress =>
    {
        var report = await _transfer.PackAsync(BuildPackRequest(), progress);
        PackReport = report;
        UnpackReport = null;
        DeltaEstimate = report.Delta;
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
    });

    [RelayCommand(CanExecute = nameof(CanUnpack))]
    private Task Unpack() => RunAsync("Розпакування картинок", async progress =>
    {
        var report = await _transfer.UnpackAsync(
            new UnpackRequest(ImagesFolder, RestoreFolder, SkipSignature, RememberAfterUnpack, Priority), progress);
        UnpackReport = report;
        PackReport = null;
        DeltaEstimate = null;
    });

    private PackRequest BuildPackRequest() => new(
        SourceFolder,
        OutputFolder,
        BaseMode,
        BaseFolder,
        SnapshotPath,
        EffectiveExcludes(),
        Parts,
        Threads,
        StepBits,
        CompressionLevel,
        ChunkMiB,
        NamePrefix,
        KeepArchive,
        RememberVersion,
        CoverFiles.ToList(),
        CoverBits,
        Priority);

    private IReadOnlyList<string> EffectiveExcludes()
    {
        var result = new List<string>(_paths.ExcludePatterns);

        foreach (var preset in Presets.Where(p => p.IsSelected))
            result.AddRange(_transfer.ExpandPresets(preset.Name));

        result.AddRange(ParseExtra());

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IEnumerable<string> ParseExtra() =>
        ExtraExcludes.Split(
            new[] { ';', ',', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task RunAsync(string title, Func<IProgress<string>, Task> action)
    {
        IsRunning = true;
        OperationTitle = title;
        ErrorText = string.Empty;
        Append($"=== {title} ===");

        var progress = new Progress<string>(Append);

        try
        {
            await action(progress);
            Append($"=== {title}: готово ===");
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Append($"ПОМИЛКА: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            OperationTitle = string.Empty;
        }
    }

    private void Append(string line)
    {
        Log.Add(line);
        while (Log.Count > MaxLogLines)
            Log.RemoveAt(0);
    }

    private void OnPathsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IComparePaths.RightPath))
        {
            OnPropertyChanged(nameof(SourceFolder));
            PackCommand.NotifyCanExecuteChanged();
            DryRunCommand.NotifyCanExecuteChanged();
            EstimateCommand.NotifyCanExecuteChanged();
            CreateSnapshotCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName == nameof(IComparePaths.LeftPath))
        {
            OnPropertyChanged(nameof(BaseFolder));
            OnPropertyChanged(nameof(BaseDescription));
            PackCommand.NotifyCanExecuteChanged();
            DryRunCommand.NotifyCanExecuteChanged();
            EstimateCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName == nameof(IComparePaths.ExcludePatterns))
            OnPropertyChanged(nameof(EffectiveExcludesText));
    }

    private void OnTransferChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanPackWithKey));
        OnPropertyChanged(nameof(CanUnpackWithKey));
        OnPropertyChanged(nameof(KeySummary));
        PackCommand.NotifyCanExecuteChanged();
        DryRunCommand.NotifyCanExecuteChanged();
        UnpackCommand.NotifyCanExecuteChanged();
    }
}
