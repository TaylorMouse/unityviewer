using System.Collections.ObjectModel;
using UnityBrowser.Unity;

namespace UnityBrowser;

/// <summary>Turns parsed Unity files into tree nodes. Safe to run off the UI thread.</summary>
public static class TreeBuilder
{
    public static List<TreeNode> BuildFileContents(LoadedFile file)
    {
        var result = new List<TreeNode>();

        if (file.Bundle is { } bundle)
        {
            foreach (var node in bundle.Nodes)
            {
                var sf = file.SerializedFiles.FirstOrDefault(s => s.Name == node.Path);
                result.Add(sf != null ? BuildSerializedFile(sf, node) : BuildResource(node));
            }
        }
        else
        {
            foreach (var sf in file.SerializedFiles)
                result.AddRange(BuildSerializedFile(sf, null).Children);
        }
        return result;
    }

    public static void DescribeFile(TreeNode node, LoadedFile file)
    {
        var p = node.Properties;
        p.Clear();
        p.Add(new("Path", file.Path));
        p.Add(new("Size on disk", FormatSize(file.DiskSize)));
        if (file.Bundle is { } b)
        {
            p.Add(new("Format", $"{b.Signature} v{b.Version}"));
            p.Add(new("Player version", b.UnityVersion));
            p.Add(new("Engine version", b.UnityRevision));
            p.Add(new("Declared size", FormatSize(b.Size)));
            p.Add(new("Flags", $"0x{b.Flags:X}"));
            p.Add(new("Blocks info compression", Compression.Name(b.BlocksInfoCompression)));
            p.Add(new("Blocks info at end", ((b.Flags & BundleFile.FlagBlocksInfoAtEnd) != 0).ToString()));
            var compressions = b.Blocks.Select(x => Compression.Name(x.CompressionType)).Distinct();
            p.Add(new("Data blocks", $"{b.Blocks.Count} ({string.Join(", ", compressions)})"));
            p.Add(new("Uncompressed data", FormatSize(b.Data.LongLength)));
            p.Add(new("Entries", b.Nodes.Count.ToString()));
        }
        else
        {
            p.Add(new("Format", "Serialised file (no bundle)"));
        }
        int objects = file.SerializedFiles.Sum(s => s.Objects.Count);
        p.Add(new("Objects", objects.ToString("N0")));
        node.Detail = file.Bundle != null
            ? $"{file.Bundle.Nodes.Count} entries, {objects:N0} objects"
            : $"{objects:N0} objects";
    }

    private static TreeNode BuildResource(BundleNode node)
    {
        var t = new TreeNode(NodeKind.Resource, node.Path)
        {
            Model = node,
            Detail = FormatSize(node.Size),
        };
        t.Properties.Add(new("Entry", node.Path));
        t.Properties.Add(new("Kind", "Resource data"));
        t.Properties.Add(new("Offset", $"0x{node.Offset:X}"));
        t.Properties.Add(new("Size", FormatSize(node.Size)));
        t.Properties.Add(new("Flags", $"0x{node.Flags:X}"));
        return t;
    }

    private static TreeNode BuildSerializedFile(SerializedFile sf, BundleNode? node)
    {
        var t = new TreeNode(NodeKind.SerializedFile, sf.Name)
        {
            Model = sf,
            Detail = $"{sf.Objects.Count:N0} objects",
        };
        var p = t.Properties;
        p.Add(new("Entry", sf.Name));
        p.Add(new("Kind", "Serialised file"));
        if (node != null)
        {
            p.Add(new("Offset", $"0x{node.Offset:X}"));
            p.Add(new("Flags", $"0x{node.Flags:X}"));
        }
        p.Add(new("Size", FormatSize(sf.Length)));
        p.Add(new("Format version", sf.Version.ToString()));
        p.Add(new("Unity version", string.IsNullOrEmpty(sf.UnityVersion) ? "(none)" : sf.UnityVersion));
        p.Add(new("Target platform", PlatformName(sf.TargetPlatform)));
        p.Add(new("Endianness", sf.BigEndian ? "Big" : "Little"));
        p.Add(new("Type trees", sf.EnableTypeTree ? "Yes" : "No (stripped)"));
        p.Add(new("Types", sf.Types.Count.ToString()));
        p.Add(new("Objects", sf.Objects.Count.ToString("N0")));
        p.Add(new("Data offset", $"0x{sf.DataOffset:X}"));
        for (int i = 0; i < sf.Externals.Count; i++)
            p.Add(new($"External {i + 1}", sf.Externals[i].PathName));

        var container = ObjectNames.GetContainer(sf);

        var groups = sf.Objects
            .GroupBy(o => o.TypeName)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var children = new ObservableCollection<TreeNode>();
        foreach (var group in groups)
        {
            var objects = group
                .Select(o => BuildObject(sf, o, container))
                .OrderBy(n => n.Header, StringComparer.OrdinalIgnoreCase);
            var groupNode = new TreeNode(NodeKind.TypeGroup, group.Key)
            {
                Detail = $"({group.Count():N0})",
                Children = new ObservableCollection<TreeNode>(objects),
            };
            long bytes = group.Sum(o => (long)o.ByteSize);
            groupNode.Properties.Add(new("Type", group.Key));
            groupNode.Properties.Add(new("Class ID", group.First().ClassId.ToString()));
            groupNode.Properties.Add(new("Objects", group.Count().ToString("N0")));
            groupNode.Properties.Add(new("Total data", FormatSize(bytes)));
            children.Add(groupNode);
        }
        t.Children = children;
        return t;
    }

    private static TreeNode BuildObject(SerializedFile sf, ObjectInfo o, Dictionary<long, string> container)
    {
        var name = ObjectNames.GetName(sf, o);
        container.TryGetValue(o.PathId, out var containerPath);

        string header = !string.IsNullOrEmpty(name) ? name
            : containerPath != null ? System.IO.Path.GetFileName(containerPath)
            : $"#{o.PathId}";

        var t = new TreeNode(NodeKind.Object, header)
        {
            Model = new ObjectRef(sf, o),
            Detail = FormatSize(o.ByteSize),
        };
        var p = t.Properties;
        p.Add(new("Name", name ?? "(none)"));
        p.Add(new("Type", o.TypeName));
        p.Add(new("Class ID", o.ClassId.ToString()));
        p.Add(new("Path ID", o.PathId.ToString()));
        if (containerPath != null) p.Add(new("Container", containerPath));
        p.Add(new("Offset", $"0x{o.ByteStart - sf.BaseOffset:X}"));
        p.Add(new("Size", FormatSize(o.ByteSize)));
        p.Add(new("Serialised file", sf.Name));
        return t;
    }

    public static string PlatformName(int platform) => platform switch
    {
        2 => "macOS (2)",
        5 => "Windows (5)",
        9 => "iOS (9)",
        13 => "Android (13)",
        19 => "Windows 64-bit (19)",
        20 => "WebGL (20)",
        24 => "Linux 64-bit (24)",
        31 => "PS4 (31)",
        33 => "Xbox One (33)",
        38 => "Switch (38)",
        44 => "PS5 (44)",
        _ => platform.ToString(),
    };

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
    }
}
