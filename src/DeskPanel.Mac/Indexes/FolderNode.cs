using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DeskPanel.Mac.Indexes;

/// <summary>
/// Lazily loaded folder tree node. The shared selection set, not the nodes, is the source of truth, because selections
/// can sit deep in branches that were never expanded in this session.
/// </summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private static readonly EnumerationOptions DirectoryOptions = new()
    {
        IgnoreInaccessible = true,

        // On macOS this also skips dot-folders, which is what the Windows build's Hidden filter does for its own.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        RecurseSubdirectories = false,
    };

    private readonly HashSet<string> _selection;
    private bool _childrenLoaded;
    private bool _isChecked;
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
        Children.Add(new FolderNode());
    }

    private FolderNode()
    {
        Name = "…";
        RelativePath = "";
        FullPath = "";
        _selection = new HashSet<string>();
        _childrenLoaded = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string RelativePath { get; }

    public string FullPath { get; }

    public FolderNode? Parent { get; }

    public ObservableCollection<FolderNode> Children { get; } = new();

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            if (value)
            {
                _selection.Add(RelativePath);
            }
            else
            {
                _selection.Remove(RelativePath);
            }

            Raise(nameof(IsChecked));
            NotifyCoverageChanged();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            Raise(nameof(IsExpanded));
            if (value)
            {
                EnsureChildrenLoaded();
            }
        }
    }

    /// <summary>A checked ancestor already includes this folder recursively, so the checkbox is redundant.</summary>
    public bool IsSelectable
    {
        get
        {
            for (var node = Parent; node is not null; node = node.Parent)
            {
                if (node.IsChecked)
                {
                    return false;
                }
            }

            return true;
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

        List<string> directories;
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

            child.Raise(nameof(IsSelectable));
            child.NotifyCoverageChanged();
        }
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
