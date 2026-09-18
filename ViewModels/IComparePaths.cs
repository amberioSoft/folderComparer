using System.ComponentModel;

namespace FolderDiff.ViewModels;

public interface IComparePaths : INotifyPropertyChanged
{
    string LeftPath { get; }

    string RightPath { get; }

    IReadOnlyList<string> ExcludePatterns { get; }
}
