using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDiff.Models;

namespace FolderDiff.ViewModels;

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
