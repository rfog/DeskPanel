using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DeskPanel.App.Indexes;

/// <summary>
/// Lazily loaded folder tree node. The shared <see cref="Selection"/> set, not the nodes, is the source of truth,
/// because selections can sit deep in branches that were never expanded in this session.
/// </summary>
internal sealed partial class FolderNode : ObservableObject
{
    private static readonly EnumerationOptions DirectoryOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        RecurseSubdirectories = false,
    };

    private readonly HashSet<string> _selection;
    private bool _childrenLoaded;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private bool _isExpanded;

    public FolderNode(string name, string relativePath, string fullPath, FolderNode? parent, HashSet<string> selection)
    {
        Name = name;
        RelativePath = relativePath;
        FullPath = fullPath;
        Parent = parent;
        _selection = selection;
        _isChecked = selection.Contains(relativePath);

        // Placeholder child so the expander arrow shows without enumerating the (possibly remote) folder up front.
        Children.Add(PlaceholderNode);
    }

    private FolderNode()
    {
        Name = "…";
        RelativePath = "";
        FullPath = "";
        _selection = new HashSet<string>();
        _childrenLoaded = true;
    }

    private static FolderNode PlaceholderNode => new();

    public string Name { get; }
    public string RelativePath { get; }
    public string FullPath { get; }
    public FolderNode? Parent { get; }
    public ObservableCollection<FolderNode> Children { get; } = new();

    /// <summary>A checked ancestor already includes this folder recursively, so the checkbox is redundant.</summary>
    public bool IsCoveredByAncestor
    {
        get
        {
            for (var node = Parent; node is not null; node = node.Parent)
            {
                if (node.IsChecked)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool IsSelectable => !IsCoveredByAncestor;

    partial void OnIsCheckedChanged(bool value)
    {
        if (value)
        {
            _selection.Add(RelativePath);
        }
        else
        {
            _selection.Remove(RelativePath);
        }

        NotifyCoverageChanged();
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            EnsureChildrenLoaded();
        }
    }

    public void EnsureChildrenLoaded()
    {
        if (_childrenLoaded)
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear();

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(FullPath, "*", DirectoryOptions)
                .OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            var relative = RelativePath.Length == 0 ? name : $"{RelativePath}/{name}";
            var child = new FolderNode(name, relative, directory, this, _selection);
            Children.Add(child);

            // Open the path down to existing selections so they are visible without hunting for them.
            if (_selection.Any(s => s.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
            {
                child.IsExpanded = true;
            }
        }
    }

    private void NotifyCoverageChanged()
    {
        foreach (var child in Children)
        {
            if (child.Parent is null)
            {
                continue;
            }

            child.OnPropertyChanged(nameof(IsCoveredByAncestor));
            child.OnPropertyChanged(nameof(IsSelectable));
            child.NotifyCoverageChanged();
        }
    }
}
