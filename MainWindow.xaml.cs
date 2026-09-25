using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;
using UnityBrowser.Export;
using UnityBrowser.Unity;

namespace UnityBrowser;

public partial class MainWindow : Window
{
    /// <summary>Asset bundle extensions picked up by Open Folder.</summary>
    private static readonly string[] FolderPatterns = { "*.unity3d", "*.bundle" };

    private readonly ObservableCollection<TreeNode> _roots = new();

    private CancellationTokenSource? _previewCts;
    private DecodedImage? _image;
    private BitmapSource? _bitmap;
    private BitmapSource? _opaqueBitmap;
    private double _zoom = 1;

    // Export selection (Ctrl/Shift+click), separate from the single focused item that drives the viewer.
    private readonly List<TreeNode> _selection = new();
    private TreeNode? _anchor;
    private bool _keepSelectionOnFocusChange;
    private bool _exporting;

    public MainWindow()
    {
        AppSettings.Load();
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(1, 0, 0);
        Title = $"Unity Viewer by Taylor Mouse (v{version.Major}.{version.Minor}.{version.Build})";
        InitAudio();
        Tree.ItemsSource = _roots;
        TypesTree.ItemsSource = _typeRoots;

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
            Loaded += (_, _) =>
            {
                try
                {
                    OpenPath(args[1]);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not open {args[1]}:\n{ex}", "Unity Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e) => ShowOpenFile();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => ShowOpenFolder();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopAudio();
        StopAnimation();
        CleanAudioTemp();
        AppSettings.Save();
    }

    private void Preferences_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PreferencesWindow { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            string folder = AppSettings.Current.ExportFolder;
            StatusText.Text = folder.Length > 0 ? $"Default export location: {folder}" : "Default export location cleared.";
        }
    }

    private void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        _roots.Clear();
        FileRegistry.Clear();
        ClearTypes();
        ShowProperties(null);
        StatusText.Text = "Closed.";
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var root in _roots.Concat(_typeRoots)) Collapse(root);
    }

    private static void Collapse(TreeNode node)
    {
        foreach (var child in node.Children) Collapse(child);
        node.IsExpanded = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not TextBox)
        {
            if (AudioPanel.Visibility == Visibility.Visible)
            {
                TogglePlayPause();
                e.Handled = true;
                return;
            }
            if (AnimationBar.Visibility == Visibility.Visible)
            {
                ToggleAnimation();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowOpenFile();
            e.Handled = true;
        }
        else if (e.Key == Key.O && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ShowOpenFolder();
            e.Handled = true;
        }
        else if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _ = ExportSelectedAsync(ExportFormat.Png);
            e.Handled = true;
        }
        else if (e.Key == Key.E && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            _ = ExportSelectedAsync(ExportFormat.Dds);
            e.Handled = true;
        }
        else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _ = FindDependenciesAsync(chooseFolder: false);
            e.Handled = true;
        }
        else if (e.Key == Key.R && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            _ = ExportRawAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            _ = ExportAllAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.W && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            _ = ExportSelectedAsync(ExportFormat.Wav);
            e.Handled = true;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            OpenPath(paths[0]);
    }

    private void ShowOpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Unity file",
            Filter = "Unity files (*.unity3d;*.bundle;*.ab;*.assets)|*.unity3d;*.bundle;*.ab;*.assets|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName);
    }

    private void ShowOpenFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Open folder of .unity3d / .bundle files" };
        if (dlg.ShowDialog(this) == true) OpenFolder(dlg.FolderName);
    }

    private void OpenPath(string path)
    {
        if (Directory.Exists(path)) OpenFolder(path);
        else if (File.Exists(path)) OpenFile(path);
        else StatusText.Text = $"Not found: {path}";
    }

    private void OpenFile(string path)
    {
        ClearSelection();
        _roots.Clear();
        FileRegistry.Clear();
        ClearTypes();
        LeftTabs.SelectedIndex = 0;
        ShowProperties(null);
        var node = CreateFileNode(path, Path.GetFileName(path));
        _roots.Add(node);
        node.IsExpanded = true; // triggers the load
        node.IsSelected = true;
    }

    private void OpenFolder(string folder)
    {
        ClearSelection();
        _roots.Clear();
        FileRegistry.Clear();
        ClearTypes();
        LeftTabs.SelectedIndex = 0;
        ShowProperties(null);

        var files = FolderPatterns
            .SelectMany(pattern => Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var root = new TreeNode(NodeKind.Folder, folder) { Detail = $"{files.Count} files", FilePath = folder };
        root.Properties.Add(new("Folder", folder));
        root.Properties.Add(new("Pattern", string.Join(", ", FolderPatterns) + " (including subfolders)"));
        root.Properties.Add(new("Files", files.Count.ToString()));
        root.Properties.Add(new("Total size", TreeBuilder.FormatSize(files.Sum(f => new FileInfo(f).Length))));

        foreach (var file in files)
            root.Children.Add(CreateFileNode(file, Path.GetRelativePath(folder, file)));

        _roots.Add(root);
        root.IsExpanded = true;
        root.IsSelected = true;
        StatusText.Text = $"{files.Count} bundle files in {folder}. Expand a file to load it.";
    }

    private TreeNode CreateFileNode(string path, string header)
    {
        var node = new TreeNode(NodeKind.File, header)
        {
            Detail = TreeBuilder.FormatSize(new FileInfo(path).Length),
            LazyLoader = n => LoadFileNode(n, path),
            FilePath = path,
        };
        node.Properties.Add(new("Path", path));
        node.Properties.Add(new("State", "Not loaded yet (expand to load)"));
        node.Children.Add(TreeNode.Placeholder());
        return node;
    }

    private async Task LoadFileNode(TreeNode node, string path)
    {
        StatusText.Text = $"Loading {Path.GetFileName(path)}...";
        var sw = Stopwatch.StartNew();
        try
        {
            var (file, children) = await Task.Run(() =>
            {
                var f = LoadedFile.Load(path);
                return (f, TreeBuilder.BuildFileContents(f));
            });

            node.Model = file;
            FileRegistry.Register(file);
            TreeBuilder.DescribeFile(node, file);
            node.Children = new ObservableCollection<TreeNode>(children);

            // A single serialised file is the interesting level; open it straight away.
            if (children.Count(c => c.Kind == NodeKind.SerializedFile) == 1)
                children.First(c => c.Kind == NodeKind.SerializedFile).IsExpanded = true;

            int objects = file.SerializedFiles.Sum(s => s.Objects.Count);
            StatusText.Text = $"Loaded {Path.GetFileName(path)}: {objects:N0} objects in {sw.ElapsedMilliseconds} ms.";
        }
        catch (Exception ex)
        {
            node.Properties.Clear();
            node.Properties.Add(new("Path", path));
            node.Properties.Add(new("Error", ex.Message));
            node.Detail = "failed";
            node.Children = new ObservableCollection<TreeNode> { new(NodeKind.Error, ex.Message) };
            StatusText.Text = $"Failed to load {Path.GetFileName(path)}: {ex.Message}";
        }

        if (node.IsSelected && ActiveTree == Tree) ShowProperties(node);
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!ReferenceEquals(sender, ActiveTree)) return; // e.g. a file in the hidden tab finished loading
        var node = e.NewValue as TreeNode;
        if (!_keepSelectionOnFocusChange && node != null) SetSelection(node);
        ShowProperties(node);
    }

    // ---- Multi-selection ----

    private void ClearSelection()
    {
        foreach (var n in _selection) n.IsMultiSelected = false;
        _selection.Clear();
        _anchor = null;
    }

    private void SetSelection(TreeNode node)
    {
        ClearSelection();
        node.IsMultiSelected = true;
        _selection.Add(node);
        _anchor = node;
    }

    private void ToggleSelection(TreeNode node)
    {
        node.IsMultiSelected = !node.IsMultiSelected;
        if (node.IsMultiSelected) _selection.Add(node);
        else _selection.Remove(node);
        _anchor = node;
        UpdateSelectionStatus();
    }

    /// <summary>Shift+click: select the visible rows between the anchor and the node (Ctrl adds to the selection).</summary>
    private void SelectRange(TreeNode anchor, TreeNode node, bool add)
    {
        if (!add)
        {
            foreach (var n in _selection) n.IsMultiSelected = false;
            _selection.Clear();
        }
        var visible = VisibleNodes(ActiveRoots).ToList();
        int a = visible.IndexOf(anchor), b = visible.IndexOf(node);
        var range = a < 0 || b < 0 ? [node] : visible.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1);
        foreach (var n in range)
        {
            if (n.IsMultiSelected) continue;
            n.IsMultiSelected = true;
            _selection.Add(n);
        }
        UpdateSelectionStatus();
    }

    private static IEnumerable<TreeNode> VisibleNodes(IEnumerable<TreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            if (!n.IsExpanded) continue;
            foreach (var c in VisibleNodes(n.Children)) yield return c;
        }
    }

    private void UpdateSelectionStatus() =>
        StatusText.Text = _selection.Count == 1 ? "1 item selected." : $"{_selection.Count} items selected.";

    /// <summary>Finds the tree node under a mouse event; null when the click was on an expander arrow.</summary>
    private static TreeNode? NodeFromEvent(RoutedEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not TreeViewItem)
        {
            if (d is ToggleButton) return null;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return (d as TreeViewItem)?.DataContext as TreeNode;
    }

    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var node = NodeFromEvent(e);
        if (node == null) return;

        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        if (!ctrl && !shift)
        {
            SetSelection(node);
            return;
        }

        // The click still moves the focus (and the viewer) but must not reset the selection.
        _keepSelectionOnFocusChange = true;
        Dispatcher.BeginInvoke(() => _keepSelectionOnFocusChange = false, DispatcherPriority.Background);

        if (shift && _anchor != null) SelectRange(_anchor, node, add: ctrl);
        else ToggleSelection(node);
    }

    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var node = NodeFromEvent(e);
        if (node == null) return;
        // Right-clicking outside the selection selects that item alone, like Explorer.
        if (!node.IsMultiSelected) SetSelection(node);
        _keepSelectionOnFocusChange = true;
        node.IsSelected = true;
        _keepSelectionOnFocusChange = false;
    }

    // ---- Export ----

    private void ExportPng_Click(object sender, RoutedEventArgs e) => _ = ExportSelectedAsync(ExportFormat.Png);
    private void ExportDds_Click(object sender, RoutedEventArgs e) => _ = ExportSelectedAsync(ExportFormat.Dds);
    private void ExportWav_Click(object sender, RoutedEventArgs e) => _ = ExportSelectedAsync(ExportFormat.Wav);
    private void ExportObj_Click(object sender, RoutedEventArgs e) => _ = ExportSelectedAsync(ExportFormat.Obj);
    private void ExportDae_Click(object sender, RoutedEventArgs e) => _ = ExportSelectedAsync(ExportFormat.Dae);

    private string? ResolveExportFolder()
    {
        string folder = AppSettings.Current.ExportFolder;
        if (folder.Length > 0)
        {
            try
            {
                Directory.CreateDirectory(folder);
                return folder;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"The default export location can't be used:\n{ex.Message}\n\nChoose a folder instead.",
                    "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        var dlg = new OpenFolderDialog { Title = "Export to folder" };
        return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
    }

    private async Task ExportSelectedAsync(ExportFormat format)
    {
        if (_exporting) return;

        var roots = _selection.Count > 0 ? _selection.ToList()
            : ActiveTree.SelectedItem is TreeNode focused ? [focused] : new List<TreeNode>();

        // Gather on the UI thread (the tree is not thread-safe); unloaded files are loaded during the export.
        var objects = new List<ObjectRef>();
        var files = new List<string>();
        var seenObjects = new HashSet<(SerializedFile, long)>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddObject(SerializedFile sf, ObjectInfo o)
        {
            if (Exporter.CanExport(o, format) && seenObjects.Add((sf, o.PathId))) objects.Add(new ObjectRef(sf, o));
        }

        void Collect(TreeNode node)
        {
            switch (node.Kind)
            {
                case NodeKind.Object when node.Model is ObjectRef r:
                    AddObject(r.File, r.Info);
                    break;
                case NodeKind.File when node.Model is LoadedFile lf:
                    foreach (var sf in lf.SerializedFiles)
                        foreach (var o in sf.Objects) AddObject(sf, o);
                    break;
                case NodeKind.File when node.FilePath != null && node.Detail != "failed":
                    if (seenFiles.Add(node.FilePath)) files.Add(node.FilePath);
                    break;
                case NodeKind.SerializedFile when node.Model is SerializedFile sfm:
                    foreach (var o in sfm.Objects) AddObject(sfm, o);
                    break;
                case NodeKind.Folder or NodeKind.TypeGroup:
                    foreach (var child in node.Children) Collect(child);
                    break;
            }
        }

        foreach (var node in roots) Collect(node);

        if (objects.Count == 0 && files.Count == 0)
        {
            string what = format switch
            {
                ExportFormat.Wav => "audio clips",
                ExportFormat.Obj => "meshes",
                ExportFormat.Dae => "meshes or character animation clips",
                _ => "textures or sprites",
            };
            MessageBox.Show(this, $"There are no {what} to export as {format.ToString().ToUpperInvariant()} in the selection.\n\n" +
                $"Select {what}, or a type group, bundle, file or folder that contains them. " +
                "Use Ctrl+click and Shift+click to select several.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? folder = ResolveExportFolder();
        if (folder == null) return;

        _exporting = true;
        SetExportMenusEnabled(false);
        var exporter = new Exporter(folder, format);
        IProgress<string> progress = new Progress<string>(t => StatusText.Text = t);
        var sw = Stopwatch.StartNew();
        string label = format.ToString().ToUpperInvariant();

        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < objects.Count; i++)
                {
                    if (i % 10 == 0) progress.Report($"Exporting {label} {i + 1} of {objects.Count}...");
                    exporter.Export(objects[i].File, objects[i].Info);
                }

                for (int k = 0; k < files.Count; k++)
                {
                    progress.Report($"Exporting {label} from {Path.GetFileName(files[k])} (file {k + 1} of {files.Count}), {exporter.Result.Exported} done...");
                    try
                    {
                        var lf = LoadedFile.Load(files[k]);
                        foreach (var sf in lf.SerializedFiles)
                            foreach (var o in sf.Objects)
                                if (Exporter.CanExport(o, format)) exporter.Export(sf, o);
                    }
                    catch (Exception ex)
                    {
                        exporter.Result.Failures.Add($"{Path.GetFileName(files[k])}: {ex.Message}");
                    }
                }
            });
        }
        finally
        {
            _exporting = false;
            SetExportMenusEnabled(true);
        }

        ShowExportSummary(exporter.Result, folder, $"{label} files", sw.Elapsed);
    }

    /// <summary>Status line plus a summary box (with failures and skipped items) offering to open the folder.</summary>
    private void ShowExportSummary(ExportResult result, string folder, string what, TimeSpan elapsed, string? details = null)
    {
        StatusText.Text = $"Exported {result.Exported} {what} to {folder} in {elapsed.TotalSeconds:0.0} s" +
                          (result.Failures.Count > 0 ? $", {result.Failures.Count} failed" : "") +
                          (result.Skipped.Count > 0 ? $", {result.Skipped.Count} skipped" : "") + ".";

        static string List(List<string> items) =>
            string.Join("\n", items.Take(15)) + (items.Count > 15 ? $"\n... and {items.Count - 15} more" : "");

        string message = $"Exported {result.Exported} {what} to:\n{folder}";
        if (details != null) message += "\n\n" + details;
        if (result.Failures.Count > 0)
            message += $"\n\n{result.Failures.Count} could not be exported:\n" + List(result.Failures);
        if (result.Skipped.Count > 0)
            message += $"\n\n{result.Skipped.Count} skipped (prop animations are not exported yet):\n" +
                       List(result.Skipped.Select(x => x[..Math.Max(0, x.IndexOf(':'))]).ToList());
        message += "\n\nOpen the export folder?";
        var icon = result.Failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;
        if (MessageBox.Show(this, message, "Export", MessageBoxButton.YesNo, icon) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void ShowProperties(TreeNode? node)
    {
        ViewerTitle.Text = node?.Header ?? "Nothing selected";
        PropertyList.ItemsSource = node?.Properties.ToList();

        if (node?.Model is ObjectRef obj && AssetPreview.CanPreview(obj.Info))
            _ = ShowPreviewAsync(node, obj);
        else if (node?.Model is ObjectRef clip && Audio.AudioClipDecoder.CanDecode(clip.Info))
            _ = ShowAudioAsync(node, clip);
        else if (node?.Model is ObjectRef mesh && MeshReader.CanRead(mesh.Info))
            _ = ShowMeshAsync(node, mesh);
        else if (node?.Model is ObjectRef anim && AnimationClipReader.CanRead(anim.Info))
            _ = ShowAnimationAsync(node, anim);
        else
            HidePreview();
    }

    // ---- Image preview ----

    private void HidePreview()
    {
        _previewCts?.Cancel();
        StopAudio();
        HideMesh();
        _image = null;
        _bitmap = _opaqueBitmap = null;
        PreviewImage.Source = null;
        if (PreviewArea.Visibility != Visibility.Visible) return;
        PreviewArea.Visibility = PreviewSplitter.Visibility = PreviewToolbar.Visibility = Visibility.Collapsed;
        PreviewRow.Height = new GridLength(0);
        PropertiesRow.Height = new GridLength(1, GridUnitType.Star);
    }

    private void EnsurePreviewVisible()
    {
        if (PreviewArea.Visibility == Visibility.Visible) return;
        PreviewArea.Visibility = PreviewSplitter.Visibility = PreviewToolbar.Visibility = Visibility.Visible;
        PreviewRow.Height = new GridLength(3, GridUnitType.Star);
        PropertiesRow.Height = new GridLength(1, GridUnitType.Star);
    }

    private void ShowPreviewMessage(string text)
    {
        PreviewImage.Source = null;
        PreviewFrame.Visibility = Visibility.Collapsed;
        PreviewMessage.Text = text;
        PreviewMessage.Visibility = Visibility.Visible;
        ZoomText.Text = "";
    }

    private async Task ShowPreviewAsync(TreeNode node, ObjectRef obj)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        StopAudio();
        HideMesh();
        EnsurePreviewVisible();
        PreviewToolbar.Visibility = Visibility.Visible;
        PreviewScroll.Visibility = Visibility.Visible;
        ShowPreviewMessage("Decoding...");

        try
        {
            var (image, bitmap) = await Task.Run(() =>
            {
                var img = AssetPreview.GetImage(obj.File, obj.Info);
                return (img, CreateBitmap(img.Width, img.Height, img.Bgra));
            }, cts.Token);
            if (cts.IsCancellationRequested) return;

            _image = image;
            _bitmap = bitmap;
            _opaqueBitmap = null;
            PreviewMessage.Visibility = Visibility.Collapsed;
            PreviewFrame.Visibility = Visibility.Visible;
            ApplyImage();
            ApplyZoom();

            PropertyList.ItemsSource = image.Info.Select(i => new PropertyRow(i.Key, i.Value))
                .Concat(node.Properties).ToList();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) ShowPreviewMessage($"Can't preview this {obj.Info.TypeName}.\n{ex.Message}");
        }
    }

    private static BitmapSource CreateBitmap(int width, int height, byte[] bgra)
    {
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bmp.Freeze();
        return bmp;
    }

    private void ApplyImage()
    {
        if (_image == null || _bitmap == null) return;
        if (AlphaToggle.IsChecked == true)
        {
            PreviewImage.Source = _bitmap;
            return;
        }
        if (_opaqueBitmap == null)
        {
            var opaque = (byte[])_image.Bgra.Clone();
            for (int i = 3; i < opaque.Length; i += 4) opaque[i] = 255;
            _opaqueBitmap = CreateBitmap(_image.Width, _image.Height, opaque);
        }
        PreviewImage.Source = _opaqueBitmap;
    }

    private void ApplyZoom()
    {
        bool fit = FitToggle.IsChecked == true;
        PreviewScroll.HorizontalScrollBarVisibility = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        PreviewScroll.VerticalScrollBarVisibility = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        PreviewImage.Stretch = fit ? Stretch.Uniform : Stretch.None;
        PreviewImage.LayoutTransform = fit ? Transform.Identity : new ScaleTransform(_zoom, _zoom);
        double scale = fit ? CurrentFitScale() : _zoom;
        RenderOptions.SetBitmapScalingMode(PreviewImage,
            scale >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        ZoomText.Text = $"{scale * 100:0}%";
    }

    private double CurrentFitScale()
    {
        if (_image == null) return 1;
        double w = Math.Max(1, PreviewScroll.ActualWidth - 24), h = Math.Max(1, PreviewScroll.ActualHeight - 24);
        return Math.Min(w / _image.Width, h / _image.Height);
    }

    private void PreviewScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_image != null && FitToggle.IsChecked == true) ApplyZoom();
    }

    private void FitToggle_Click(object sender, RoutedEventArgs e)
    {
        _zoom = 1;
        ApplyZoom();
    }

    private void AlphaToggle_Click(object sender, RoutedEventArgs e) => ApplyImage();

    private void PreviewScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_image == null) return;
        if (FitToggle.IsChecked == true)
        {
            FitToggle.IsChecked = false;
            _zoom = CurrentFitScale();
        }
        _zoom = Math.Clamp(e.Delta > 0 ? _zoom * 1.25 : _zoom / 1.25, 1.0 / 32, 64);
        ApplyZoom();
        e.Handled = true;
    }
}
