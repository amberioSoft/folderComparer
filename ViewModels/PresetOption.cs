using CommunityToolkit.Mvvm.ComponentModel;

namespace FolderDiff.ViewModels;

public partial class PresetOption : ObservableObject
{
    public PresetOption(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;
}
