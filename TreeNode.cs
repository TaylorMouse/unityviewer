using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace UnityBrowser;

public enum NodeKind { Folder, File, Bundle, SerializedFile, Resource, TypeGroup, Object, Info, Error }

public sealed record PropertyRow(string Key, string Value);

/// <summary>Model of an object node: the object plus the file it lives in.</summary>
public sealed record ObjectRef(Unity.SerializedFile File, Unity.ObjectInfo Info);

public sealed class TreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isMultiSelected;
    private string _detail = "";
    private ObservableCollection<TreeNode> _children = new();

    public TreeNode(NodeKind kind, string header)
    {
        Kind = kind;
        Header = header;
    }

    public NodeKind Kind { get; }
    public string Header { get; set; }
    public object? Model { get; set; }

    /// <summary>Disk path for file nodes (set before the file is loaded).</summary>
    public string? FilePath { get; set; }

    /// <summary>Called once, the first time the node is expanded.</summary>
    public Func<TreeNode, Task>? LazyLoader { get; set; }

    public List<PropertyRow> Properties { get; } = new();

    public string Detail
    {
        get => _detail;
        set { _detail = value; OnPropertyChanged(); }
    }

    public ObservableCollection<TreeNode> Children
    {
        get => _children;
        set { _children = value; OnPropertyChanged(); }
    }

    /// <summary>Part of the export selection (Ctrl/Shift+click). Separate from the focused IsSelected item.</summary>
    public bool IsMultiSelected
    {
        get => _isMultiSelected;
        set
        {
            if (_isMultiSelected == value) return;
            _isMultiSelected = value;
            OnPropertyChanged();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            if (value && LazyLoader != null)
            {
                var loader = LazyLoader;
                LazyLoader = null;
                _ = loader(this);
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string Icon => Kind switch
    {
        NodeKind.Folder => "",
        NodeKind.File => "",
        NodeKind.Bundle => "",
        NodeKind.SerializedFile => "",
        NodeKind.Resource => "",
        NodeKind.TypeGroup => "",
        NodeKind.Object => "",
        NodeKind.Info => "",
        NodeKind.Error => "",
        _ => "",
    };

    public Brush IconBrush => Kind switch
    {
        NodeKind.Folder => Palette.Folder,
        NodeKind.File or NodeKind.Bundle => Palette.Bundle,
        NodeKind.SerializedFile => Palette.Serialized,
        NodeKind.Resource => Palette.Resource,
        NodeKind.TypeGroup => Palette.Group,
        NodeKind.Error => Palette.Error,
        _ => Palette.Plain,
    };

    public Brush HeaderBrush => Kind == NodeKind.Error ? Palette.Error : Palette.Text;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static TreeNode Placeholder() => new(NodeKind.Info, "Loading...");
}

/// <summary>Frozen brushes, safe to hand out from background threads.</summary>
public static class Palette
{
    public static readonly Brush Folder = Make(0xE8, 0xB3, 0x39);
    public static readonly Brush Bundle = Make(0x3B, 0x82, 0xD6);
    public static readonly Brush Serialized = Make(0x7A, 0x5C, 0xC7);
    public static readonly Brush Resource = Make(0x2E, 0x9E, 0x6A);
    public static readonly Brush Group = Make(0x80, 0x80, 0x80);
    public static readonly Brush Plain = Make(0x60, 0x60, 0x60);
    public static readonly Brush Error = Make(0xC8, 0x2A, 0x2A);
    public static readonly Brush Text = Make(0x1E, 0x1E, 0x1E);

    private static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
