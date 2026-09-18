using System.Collections.Specialized;
using System.Windows.Controls;

namespace FolderDiff.Views;

public partial class TransferView : UserControl
{
    private INotifyCollectionChanged? _log;

    public TransferView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_log is not null)
            _log.CollectionChanged -= OnLogChanged;

        _log = (LogList.ItemsSource as INotifyCollectionChanged);

        if (_log is not null)
            _log.CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0)
            return;

        LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
