using System.Diagnostics;
using System.Windows;
using UnityBrowser.Audio;
using UnityBrowser.Export;
using UnityBrowser.Unity;

namespace UnityBrowser;

/// <summary>
/// "Export All Known Formats": everything under the selected nodes (folders, files, groups, objects),
/// with the formats chosen in Export Preferences. Each source file gets its own subfolder.
/// </summary>
public partial class MainWindow
{
    private void ExportAll_Click(object sender, RoutedEventArgs e) => _ = ExportAllAsync();

    private void SetExportMenusEnabled(bool enabled) =>
        ExportPngMenu.IsEnabled = ExportDdsMenu.IsEnabled = ExportWavMenu.IsEnabled =
            ExportObjMenu.IsEnabled = ExportDaeMenu.IsEnabled = ExportAllMenu.IsEnabled = enabled;

    /// <summary>The export format for an object under the current preferences, or null to leave it out.</summary>
    private static ExportFormat? FormatFor(ObjectInfo o, AppSettings s) => o.TypeName switch
    {
        "Texture2D" or "Sprite" => s.ImageFormat.Equals("Dds", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Dds : ExportFormat.Png,
        "Mesh" => s.MeshFormat.Equals("Obj", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Obj : ExportFormat.Dae,
        "AnimationClip" when s.ExportAnimations => ExportFormat.Dae,
        "AudioClip" when s.ExportSound => ExportFormat.Wav,
        _ => null,
    };

    private sealed class FileJob
    {
        public required string Path { get; init; }
        /// <summary>Output subfolder, relative to the export location.</summary>
        public required string SubFolder { get; init; }
        public LoadedFile? Loaded { get; set; }
        /// <summary>The whole file was selected (a file or folder node).</summary>
        public bool Whole { get; set; }
        /// <summary>Otherwise only these objects (from group or object nodes).</summary>
        public HashSet<(SerializedFile, long)> Only { get; } = new();
    }

    private async Task ExportAllAsync()
    {
        if (_exporting) return;
        var settings = AppSettings.Current;
        var roots = _selection.Count > 0 ? _selection.ToList()
            : Tree.SelectedItem is TreeNode focused ? [focused] : new List<TreeNode>();

        // Gather per source file on the UI thread. Folder nodes set the base for the mirrored structure.
        var jobs = new Dictionary<string, FileJob>(StringComparer.OrdinalIgnoreCase);
        FileJob Job(string path, LoadedFile? loaded, string? baseFolder)
        {
            if (!jobs.TryGetValue(path, out var job))
            {
                string rel = baseFolder != null ? System.IO.Path.GetRelativePath(baseFolder, path) : System.IO.Path.GetFileName(path);
                string sub = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(rel) ?? "", System.IO.Path.GetFileNameWithoutExtension(rel));
                jobs[path] = job = new FileJob { Path = path, SubFolder = sub, Loaded = loaded };
            }
            job.Loaded ??= loaded;
            return job;
        }

        void Collect(TreeNode node, string? baseFolder)
        {
            switch (node.Kind)
            {
                case NodeKind.Folder:
                    foreach (var child in node.Children) Collect(child, node.FilePath ?? baseFolder);
                    break;
                case NodeKind.File when node.FilePath != null && node.Detail != "failed":
                    Job(node.FilePath, node.Model as LoadedFile, baseFolder).Whole = true;
                    break;
                case NodeKind.SerializedFile when node.Model is SerializedFile sf:
                    AddObjects(sf, sf.Objects, baseFolder);
                    break;
                case NodeKind.TypeGroup:
                    foreach (var child in node.Children) Collect(child, baseFolder);
                    break;
                case NodeKind.Object when node.Model is ObjectRef r:
                    AddObjects(r.File, new[] { r.Info }, baseFolder);
                    break;
            }
        }

        void AddObjects(SerializedFile sf, IEnumerable<ObjectInfo> objects, string? baseFolder)
        {
            if (sf.Owner == null) return;
            var job = Job(sf.Owner.Path, sf.Owner, baseFolder);
            foreach (var o in objects) job.Only.Add((sf, o.PathId));
        }

        foreach (var node in roots) Collect(node, null);

        if (jobs.Count == 0)
        {
            MessageBox.Show(this, "Select a folder, file or items to export first.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? folder = ResolveExportFolder();
        if (folder == null) return;

        _exporting = true;
        SetExportMenusEnabled(false);
        IProgress<string> progress = new Progress<string>(t => StatusText.Text = t);
        var sw = Stopwatch.StartNew();
        var total = new ExportResult();
        var perKind = new Dictionary<string, int>();

        try
        {
            await Task.Run(() =>
            {
                int index = 0;
                foreach (var job in jobs.Values.OrderBy(j => j.SubFolder, StringComparer.OrdinalIgnoreCase))
                {
                    index++;
                    string name = System.IO.Path.GetFileName(job.Path);
                    progress.Report($"Exporting {name} (file {index} of {jobs.Count}), {total.Exported} files written...");
                    try
                    {
                        var file = job.Loaded ?? LoadedFile.Load(job.Path);
                        string target = System.IO.Path.Combine(folder, job.SubFolder);
                        var exporters = new Dictionary<ExportFormat, Exporter>();
                        foreach (var sf in file.SerializedFiles)
                            foreach (var o in sf.Objects)
                            {
                                if (!job.Whole && !job.Only.Contains((sf, o.PathId))) continue;
                                if (FormatFor(o, settings) is not { } format || !Exporter.CanExport(o, format)) continue;
                                if (!exporters.TryGetValue(format, out var exporter))
                                    exporters[format] = exporter = new Exporter(target, format);
                                int before = exporter.Result.Exported;
                                exporter.Export(sf, o);
                                if (exporter.Result.Exported > before)
                                    perKind[o.TypeName] = perKind.GetValueOrDefault(o.TypeName) + 1;
                            }
                        foreach (var e in exporters.Values) total.Add(e.Result);
                    }
                    catch (Exception ex)
                    {
                        total.Failures.Add($"{name}: {ex.Message}");
                    }
                }
            });
        }
        finally
        {
            _exporting = false;
            SetExportMenusEnabled(true);
        }

        string formats = $"Images as {settings.ImageFormat.ToUpperInvariant()}, meshes as {settings.MeshFormat.ToUpperInvariant()}" +
                         (settings.ExportAnimations ? ", animation clips as DAE" : "") +
                         (settings.ExportSound ? ", sound as WAV" : "") + ".";
        string counts = perKind.Count == 0 ? "Nothing matched the export settings."
            : string.Join(", ", perKind.OrderBy(k => k.Key).Select(k => $"{k.Value} {k.Key}"));
        ShowExportSummary(total, folder, $"files from {jobs.Count} source file(s)", sw.Elapsed, $"{counts}\n{formats}");
    }
}
