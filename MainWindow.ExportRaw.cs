using System.Diagnostics;
using System.Windows;
using UnityBrowser.Export;
using UnityBrowser.Unity;

namespace UnityBrowser;

/// <summary>
/// "Export Selected Raw": no conversion. Objects are written as their serialised bytes (plus any payload
/// they keep elsewhere); file, bundle entry and resource nodes are written out exactly as stored.
/// </summary>
public partial class MainWindow
{
    private void ExportRaw_Click(object sender, RoutedEventArgs e) => _ = ExportRawAsync();

    private async Task ExportRawAsync()
    {
        if (_exporting) return;
        var roots = _selection.Count > 0 ? _selection.ToList()
            : ActiveTree.SelectedItem is TreeNode focused ? [focused] : new List<TreeNode>();

        // Gather on the UI thread.
        var objects = new List<ObjectRef>();
        var seenObjects = new HashSet<(SerializedFile, long)>();
        var wholeFiles = new Dictionary<string, LoadedFile?>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(LoadedFile File, BundleNode Node)>();

        void Collect(TreeNode node)
        {
            switch (node.Kind)
            {
                case NodeKind.Folder:
                case NodeKind.TypeGroup:
                    foreach (var child in node.Children) Collect(child);
                    break;
                case NodeKind.File when node.FilePath != null && node.Detail != "failed":
                    wholeFiles[node.FilePath] = node.Model as LoadedFile;
                    break;
                case NodeKind.SerializedFile when node.Model is SerializedFile sf && sf.Owner != null:
                    if (sf.Owner.Bundle?.Nodes.FirstOrDefault(n => n.Path == sf.Name) is { } entry) entries.Add((sf.Owner, entry));
                    else wholeFiles[sf.Owner.Path] = sf.Owner; // a plain .assets file: the file is the entry
                    break;
                case NodeKind.Resource when node.Model is BundleNode resource:
                    if (FileNodes().FirstOrDefault(f => f.Children.Contains(node))?.Model is LoadedFile owner) entries.Add((owner, resource));
                    break;
                case NodeKind.Object when node.Model is ObjectRef r:
                    if (seenObjects.Add((r.File, r.Info.PathId))) objects.Add(r);
                    break;
            }
        }
        foreach (var node in roots) Collect(node);

        if (objects.Count == 0 && wholeFiles.Count == 0 && entries.Count == 0)
        {
            MessageBox.Show(this, "Select objects, a type group, a bundle entry, a file or a folder to export raw.",
                "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? folder = ResolveExportFolder();
        if (folder == null) return;

        _exporting = true;
        SetExportMenusEnabled(false);
        IProgress<string> progress = new Progress<string>(t => StatusText.Text = t);
        var sw = Stopwatch.StartNew();
        var result = new ExportResult();

        try
        {
            await Task.Run(() =>
            {
                var exporter = new Exporter(folder, ExportFormat.Raw);
                for (int i = 0; i < objects.Count; i++)
                {
                    if (i % 50 == 0) progress.Report($"Exporting raw {i + 1} of {objects.Count}...");
                    exporter.Export(objects[i].File, objects[i].Info);
                }
                result.Add(exporter.Result);

                foreach (var (file, node) in entries)
                {
                    try
                    {
                        RawWriter.WriteEntry(file.Bundle!, node, folder);
                        result.Exported++;
                    }
                    catch (Exception ex)
                    {
                        result.Failures.Add($"{node.Path}: {ex.Message}");
                    }
                }

                int k = 0;
                foreach (var (path, loaded) in wholeFiles)
                {
                    progress.Report($"Exporting raw {System.IO.Path.GetFileName(path)} (file {++k} of {wholeFiles.Count})...");
                    try
                    {
                        var file = loaded ?? LoadedFile.Load(path);
                        result.Exported += RawWriter.WriteFile(file, System.IO.Path.Combine(folder, System.IO.Path.GetFileNameWithoutExtension(path)));
                    }
                    catch (Exception ex)
                    {
                        result.Failures.Add($"{System.IO.Path.GetFileName(path)}: {ex.Message}");
                    }
                }
            });
        }
        finally
        {
            _exporting = false;
            SetExportMenusEnabled(true);
        }

        ShowExportSummary(result, folder, "raw items", sw.Elapsed,
            "Objects are written as their serialised data (.bytes), with streamed data (.resS), sound banks (.fsb) and " +
            "TextAsset contents alongside; files and bundle entries exactly as stored.");
    }
}
