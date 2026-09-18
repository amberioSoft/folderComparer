using System.Windows;
using System.Windows.Controls;
using FolderDiff.Models;

namespace FolderDiff.Infrastructure;

public sealed class DiffRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? RowTemplate { get; set; }

    public DataTemplate? SeparatorTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is DiffRow { IsSeparator: true } ? SeparatorTemplate : RowTemplate;
}
