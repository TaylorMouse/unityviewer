using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using UnityBrowser.Unity;

namespace UnityBrowser;

/// <summary>
/// File > Find Dependencies: bundles refer to each other by internal name (CAB-xxxx) only. This scans a
/// folder's bundle directories (headers only, no decompression) for the names the selected files need,
/// and adds the bundles that hold them to the Files tab, so cross-bundle references (such as a skin's
/// base animations) resolve.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] BuiltInResources = { "unity default resources", "unity_builtin_extra", "library/unity default resources" };

    private void FindDependencies_Click(object sender, RoutedEventArgs e) => _ = FindDependenciesAsync(chooseFolder: false);
    private void FindDependenciesIn_Click(object sender, RoutedEventArgs e) => _ = FindDependenciesAsync(chooseFolder: true);

    /// <summary>The file nodes the command works on: those selected, or holding the selection, else all.</summary>
    private List<TreeNode> TargetFileNodes()
    {
        var all = FileNodes().ToList();
        bool Contains(TreeNode parent, TreeNode node) => parent == node || parent.Children.Any(c => Contains(c, node));
        var selected = _selection.Count > 0 ? _selection.ToList()
            : ActiveTree.SelectedItem is TreeNode n ? new List<TreeNode> { n } : new List<TreeNode>();

        var result = new List<TreeNode>();
        foreach (var node in selected)
        {
            if (node.Kind == NodeKind.File) result.Add(node);
            else if (node.Kind == NodeKind.Folder) result.AddRange(FileNodes().Where(f => Contains(node, f)));
            else if (node.Model is ObjectRef r && all.FirstOrDefault(f => f.Model == r.File.Owner) is { } owner) result.Add(owner);
            else if (all.FirstOrDefault(f => Contains(f, node)) is { } holder) result.Add(holder);
        }
        return (result.Count > 0 ? result : all).Distinct().ToList();
    }

    /// <summary>Searches the remembered folder, or asks for one (the first time, or with Find Dependencies In...).</summary>
    private async Task FindDependenciesAsync(bool chooseFolder)
    {
        var targets = TargetFileNodes();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "Open a file or folder first (File menu).", "Find Dependencies", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var t in targets) await EnsureLoadedAsync(t);

        // Internal names the targets refer to that are not open yet.
        var needed = targets.Select(t => t.Model).OfType<LoadedFile>()
            .SelectMany(f => f.SerializedFiles.SelectMany(sf => sf.Externals))
            .Select(FileRegistry.ExternalName)
            .Where(n => !BuiltInResources.Contains(n, StringComparer.OrdinalIgnoreCase) && !FileRegistry.IsOpen(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (needed.Count == 0)
        {
            MessageBox.Show(this, "All dependencies of the selected file(s) are already open.", "Find Dependencies",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string folder = AppSettings.Current.DependencyFolder;
        if (chooseFolder || !Directory.Exists(folder))
        {
            var dlg = new OpenFolderDialog { Title = $"Folder to search for {needed.Count} dependency bundle(s)" };
            string start = Directory.Exists(folder) ? folder : System.IO.Path.GetDirectoryName(targets[0].FilePath) ?? "";
            if (Directory.Exists(start)) dlg.InitialDirectory = start;
            if (dlg.ShowDialog(this) != true) return;
            folder = dlg.FolderName;
            AppSettings.Current.DependencyFolder = folder;
        }

        StatusText.Text = $"Searching {folder} for {needed.Count} dependency bundle(s)...";
        var sw = Stopwatch.StartNew();
        var openPaths = FileNodes().Select(f => f.FilePath).Where(p => p != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (found, scanned) = await Task.Run(() =>
        {
            var want = new HashSet<string>(needed, StringComparer.OrdinalIgnoreCase);
            var hits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            foreach (var path in FolderPatterns.SelectMany(p => Directory.EnumerateFiles(folder, p, SearchOption.AllDirectories)))
            {
                if (hits.Count == want.Count) break;
                try
                {
                    var entries = BundleFile.ReadDirectory(path);
                    if (entries == null) continue;
                    count++;
                    foreach (var entry in entries)
                        if (want.Contains(entry.Path)) hits.TryAdd(entry.Path, path);
                }
                catch
                {
                    // Unreadable file: skip it.
                }
            }
            return (hits, count);
        });

        // Add the bundles that were found (once each) and load them so their names resolve.
        var added = new List<TreeNode>();
        foreach (var path in found.Values.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !openPaths.Contains(p)))
        {
            var node = CreateFileNode(path, $"{System.IO.Path.GetFileName(path)}  (dependency)");
            _roots.Add(node);
            added.Add(node);
        }
        foreach (var node in added) await EnsureLoadedAsync(node);

        var lines = needed.Select(n => found.TryGetValue(n, out var p)
            ? $"{n}\n    -> {System.IO.Path.GetRelativePath(folder, p)}"
            : $"{n}\n    -> not found in this folder");
        StatusText.Text = $"Found {found.Count} of {needed.Count} dependencies in {scanned:N0} bundles ({sw.Elapsed.TotalSeconds:0.0} s).";
        MessageBox.Show(this,
            $"Searched {scanned:N0} bundles in {folder} ({sw.Elapsed.TotalSeconds:0.0} s) and found {found.Count} of {needed.Count}:\n\n" +
            string.Join("\n", lines) +
            (added.Count > 0 ? $"\n\n{added.Count} bundle(s) added to the Files tab." : "") +
            "\n\nUse File > Find Dependencies In... to search another folder.",
            "Find Dependencies", MessageBoxButton.OK, found.Count == needed.Count ? MessageBoxImage.Information : MessageBoxImage.Warning);

        // A clip that was waiting for its base animation can now play layered.
        if (ActiveTree.SelectedItem is TreeNode current) ShowProperties(current);
    }
}
