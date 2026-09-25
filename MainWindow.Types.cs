using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using UnityBrowser.Unity;

namespace UnityBrowser;

/// <summary>
/// The Types tab: lists every model, texture, sound or animation across all files in the Files tree.
/// Unloaded files are loaded as part of the search.
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<TreeNode> _typeRoots = new();
    private bool _listing;

    private TreeView ActiveTree => LeftTabs.SelectedItem == TypesTab ? TypesTree : Tree;
    private IEnumerable<TreeNode> ActiveRoots => ActiveTree == TypesTree ? _typeRoots : _roots;

    private void ListModels_Click(object sender, RoutedEventArgs e) => _ = ListTypesAsync("Models", new[] { "Mesh" }, grouped: false);
    private void ListTextures_Click(object sender, RoutedEventArgs e) => _ = ListTypesAsync("Textures", new[] { "Texture2D", "Sprite" }, grouped: true);
    private void ListSounds_Click(object sender, RoutedEventArgs e) => _ = ListTypesAsync("Sounds", new[] { "AudioClip" }, grouped: false);
    private void ListAnimations_Click(object sender, RoutedEventArgs e) => _ = ListTypesAsync("Animations", new[] { "AnimationClip" }, grouped: false);

    /// <summary>Switching tabs: the export selection and the viewer follow the tree that is now visible.</summary>
    private void LeftTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, LeftTabs) || !IsLoaded) return;
        ClearSelection();
        var node = ActiveTree.SelectedItem as TreeNode;
        if (node != null) SetSelection(node);
        ShowProperties(node);
    }

    private void ClearTypes()
    {
        _typeRoots.Clear();
        TypesHeader.Text = "Use the Types menu to list all models, textures, sounds or animations in the open files.";
    }

    /// <summary>All file nodes in the Files tree (inside opened folders too).</summary>
    private IEnumerable<TreeNode> FileNodes()
    {
        IEnumerable<TreeNode> Walk(TreeNode n) => n.Kind == NodeKind.File ? new[] { n } : n.Children.SelectMany(Walk);
        return _roots.SelectMany(Walk);
    }

    /// <summary>Loads a file node the same way expanding it would, and waits for it.</summary>
    private async Task EnsureLoadedAsync(TreeNode fileNode)
    {
        if (fileNode.Model is LoadedFile) return;
        if (fileNode.LazyLoader is { } loader)
        {
            fileNode.LazyLoader = null;
            await loader(fileNode);
            return;
        }
        // Already loading (the user expanded it): wait for that to finish.
        while (fileNode.Model == null && fileNode.Detail != "failed") await Task.Delay(100);
    }

    private async Task ListTypesAsync(string title, string[] typeNames, bool grouped)
    {
        if (_listing) return;
        var files = FileNodes().ToList();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "Open a file or folder first (File menu).", "Types", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _listing = true;
        TypesMenu.IsEnabled = false;
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                if (files[i].Model is LoadedFile) continue;
                StatusText.Text = $"Listing {title.ToLowerInvariant()}: loading {files[i].Header} (file {i + 1} of {files.Count})...";
                await EnsureLoadedAsync(files[i]);
            }

            // Copy the matching object nodes from the Files tree (a node can only live in one tree).
            var found = typeNames.ToDictionary(t => t, _ => new List<TreeNode>());
            int filesWithHits = 0;
            foreach (var file in files.Where(f => f.Model is LoadedFile))
            {
                string fileName = System.IO.Path.GetFileName(file.FilePath ?? file.Header);
                bool hit = false;
                foreach (var group in file.Children.Where(c => c.Kind == NodeKind.SerializedFile).SelectMany(c => c.Children)
                             .Where(g => g.Kind == NodeKind.TypeGroup && found.ContainsKey(g.Header)))
                {
                    foreach (var obj in group.Children.Where(o => o.Kind == NodeKind.Object))
                    {
                        found[group.Header].Add(CopyForTypes(obj, fileName, file.FilePath ?? ""));
                        hit = true;
                    }
                }
                if (hit) filesWithHits++;
            }

            static IOrderedEnumerable<TreeNode> Sorted(IEnumerable<TreeNode> nodes) =>
                nodes.OrderBy(n => n.Header, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Detail, StringComparer.OrdinalIgnoreCase);

            ClearSelection();
            _typeRoots.Clear();
            int total = found.Values.Sum(l => l.Count);
            if (grouped)
            {
                foreach (var type in typeNames)
                {
                    var group = new TreeNode(NodeKind.TypeGroup, type)
                    {
                        Detail = $"({found[type].Count:N0})",
                        Children = new ObservableCollection<TreeNode>(Sorted(found[type])),
                    };
                    group.Properties.Add(new("Type", type));
                    group.Properties.Add(new("Objects", found[type].Count.ToString("N0")));
                    _typeRoots.Add(group);
                }
            }
            else
            {
                foreach (var n in Sorted(found.Values.SelectMany(l => l))) _typeRoots.Add(n);
            }

            string counts = grouped ? " (" + string.Join(", ", typeNames.Select(t => $"{found[t].Count:N0} {t}")) + ")" : "";
            TypesHeader.Text = $"{title}: {total:N0}{counts} in {filesWithHits} of {files.Count} file(s).";
            LeftTabs.SelectedItem = TypesTab;
            StatusText.Text = $"Listed {total:N0} {title.ToLowerInvariant()} from {files.Count} file(s).";
        }
        finally
        {
            _listing = false;
            TypesMenu.IsEnabled = true;
        }
    }

    private static TreeNode CopyForTypes(TreeNode source, string fileName, string filePath)
    {
        var copy = new TreeNode(NodeKind.Object, source.Header)
        {
            Model = source.Model,
            Detail = string.IsNullOrEmpty(source.Detail) ? fileName : $"{fileName} · {source.Detail}",
        };
        copy.Properties.AddRange(source.Properties);
        copy.Properties.Add(new("Source file", filePath));
        return copy;
    }
}
