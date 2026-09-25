using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using UnityBrowser.Unity;

namespace UnityBrowser;

/// <summary>Mesh preview: WPF 3D with an orbit camera (rotate, pan, zoom) and a headlight.</summary>
public partial class MainWindow
{
    private static readonly Color[] SubMeshTints =
    {
        Color.FromRgb(0xC8, 0xC8, 0xC8), Color.FromRgb(0x7F, 0xB3, 0xE8), Color.FromRgb(0xE8, 0xA8, 0x6F),
        Color.FromRgb(0x8F, 0xD1, 0x8F), Color.FromRgb(0xD9, 0x8C, 0xC9), Color.FromRgb(0xE3, 0xD3, 0x6B),
        Color.FromRgb(0x8C, 0xD5, 0xD1), Color.FromRgb(0xB4, 0x9C, 0xE6),
    };

    private MeshGeometry3D[]? _meshParts;
    private Point3D _orbitTarget;
    private double _orbitYaw, _orbitPitch, _orbitDistance, _orbitRadius = 1;
    private Point _lastMouse;
    private MouseButton? _dragButton;
    private readonly DirectionalLight _headLight = new(Color.FromRgb(0xE6, 0xE6, 0xE6), new Vector3D(0, 0, -1));

    private async Task ShowMeshAsync(TreeNode node, ObjectRef obj)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        StopAudio();
        EnsurePreviewVisible();
        PreviewToolbar.Visibility = Visibility.Collapsed;
        PreviewScroll.Visibility = Visibility.Visible;
        HideMesh();
        ShowPreviewMessage("Loading mesh...");

        try
        {
            var (mesh, parts, skeleton) = await Task.Run(() =>
            {
                var m = MeshReader.Read(obj.File, obj.Info);
                Skeleton? sk = null;
                try { sk = SkeletonReader.Read(obj.File, obj.Info, m); } catch { /* shown as unavailable */ }
                return (m, BuildGeometry(m), sk);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (mesh.TriangleCount == 0) throw new InvalidDataException("Mesh has no triangles.");

            _meshParts = parts;
            BuildScene();

            PreviewMessage.Visibility = Visibility.Collapsed;
            PreviewScroll.Visibility = Visibility.Collapsed;
            MeshPanel.Visibility = Visibility.Visible;
            MeshToolbar.Visibility = Visibility.Visible;
            MeshPanel.UpdateLayout();
            FrameMesh(mesh);

            var (min, max) = mesh.Bounds();
            var info = new List<PropertyRow>
            {
                new("Vertices", mesh.VertexCount.ToString("N0")),
                new("Triangles", mesh.TriangleCount.ToString("N0")),
                new("Sub-meshes", mesh.SubMeshes.Count == 1 ? "1"
                    : $"{mesh.SubMeshes.Count} ({string.Join(", ", mesh.SubMeshes.Select(s => (s.Triangles.Length / 3).ToString("N0")))} triangles)"),
                new("Size", $"{max[0] - min[0]:0.###} x {max[1] - min[1]:0.###} x {max[2] - min[2]:0.###}"),
                new("Attributes", string.Join(", ", new[]
                {
                    mesh.Normals != null ? "normals" : null, mesh.Uv0 != null ? "UV" : null,
                    mesh.Colors != null ? "vertex colours" : null,
                }.Where(a => a != null))),
            };
            if (mesh.IsSkinned)
            {
                info.Add(new("Skinned", $"Yes, {mesh.BoneCount} bones"));
                info.Add(new("Skeleton", skeleton == null ? "Not available"
                    : skeleton.FromScene
                        ? $"{skeleton.Nodes.Count} nodes, names and hierarchy from {skeleton.Source} (exported to DAE in bind pose)"
                        : $"{skeleton.Nodes.Count} unnamed bones rebuilt from bind poses (no SkinnedMeshRenderer found)"));
                if (skeleton?.RepairedBindPoses > 0)
                    info.Add(new("Repaired", $"{skeleton.RepairedBindPoses} broken bind pose(s) in the source data, rebuilt from the hierarchy"));
            }
            PropertyList.ItemsSource = info.Concat(node.Properties).ToList();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) ShowPreviewMessage($"Can't show this mesh.\n{ex.Message}");
        }
    }

    private void HideMesh()
    {
        StopAnimation();
        MeshPanel.Visibility = Visibility.Collapsed;
        MeshToolbar.Visibility = Visibility.Collapsed;
        MeshScene.Children.Clear();
        _meshParts = null;
    }

    /// <summary>One frozen geometry per sub-mesh (built off the UI thread).</summary>
    private static MeshGeometry3D[] BuildGeometry(MeshData m)
    {
        var positions = new Point3DCollection(m.VertexCount);
        for (int i = 0; i < m.VertexCount; i++)
            positions.Add(new Point3D(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]));
        positions.Freeze();

        Vector3DCollection? normals = null;
        if (m.Normals != null)
        {
            normals = new Vector3DCollection(m.VertexCount);
            for (int i = 0; i < m.VertexCount; i++)
                normals.Add(new Vector3D(m.Normals[i * 3], m.Normals[i * 3 + 1], m.Normals[i * 3 + 2]));
            normals.Freeze();
        }

        return m.SubMeshes.Where(s => s.Triangles.Length > 0).Select(s =>
        {
            var g = new MeshGeometry3D { Positions = positions, TriangleIndices = new Int32Collection(s.Triangles) };
            if (normals != null) g.Normals = normals;
            g.Freeze();
            return g;
        }).ToArray();
    }

    private void BuildScene()
    {
        MeshScene.Children.Clear();
        MeshScene.Children.Add(new AmbientLight(Color.FromRgb(0x46, 0x46, 0x4C)));
        MeshScene.Children.Add(_headLight);
        MeshScene.Children.Add(new DirectionalLight(Color.FromRgb(0x50, 0x55, 0x60), new Vector3D(0.4, 1, 0.3))); // fill from below
        if (_meshParts == null) return;

        bool tint = MeshSubmeshColours.IsChecked == true;
        for (int i = 0; i < _meshParts.Length; i++)
        {
            var colour = tint ? SubMeshTints[i % SubMeshTints.Length] : SubMeshTints[0];
            var front = new MaterialGroup();
            front.Children.Add(new DiffuseMaterial(new SolidColorBrush(colour)));
            front.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 40));
            var back = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x70, 0x50, 0x50)));
            MeshScene.Children.Add(new GeometryModel3D(_meshParts[i], front) { BackMaterial = back });
        }
    }

    private void FrameMesh(MeshData mesh)
    {
        var (min, max) = mesh.Bounds();
        FrameBounds(min.Select(v => (double)v).ToArray(), max.Select(v => (double)v).ToArray());
    }

    private void FrameBounds(double[] min, double[] max)
    {
        _orbitTarget = new Point3D((min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2);
        double dx = max[0] - min[0], dy = max[1] - min[1], dz = max[2] - min[2];
        _orbitRadius = Math.Max(1e-4, Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2);
        ResetOrbit();
    }

    private void ResetOrbit()
    {
        // Unity models face +Z; start slightly to the side and above.
        _orbitYaw = 25 * Math.PI / 180;
        _orbitPitch = 12 * Math.PI / 180;
        // WPF's FieldOfView is horizontal; fit the bounding sphere into the narrower of the two directions.
        double halfH = MeshCamera.FieldOfView / 2 * Math.PI / 180;
        double aspect = MeshPanel.ActualWidth > 0 && MeshPanel.ActualHeight > 0
            ? MeshPanel.ActualHeight / MeshPanel.ActualWidth : 0.6;
        double halfV = Math.Atan(Math.Tan(halfH) * aspect);
        _orbitDistance = _orbitRadius / Math.Sin(Math.Min(halfH, halfV)) * 1.05;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var dir = new Vector3D(
            Math.Cos(_orbitPitch) * Math.Sin(_orbitYaw),
            Math.Sin(_orbitPitch),
            Math.Cos(_orbitPitch) * Math.Cos(_orbitYaw));
        MeshCamera.Position = _orbitTarget + dir * _orbitDistance;
        MeshCamera.LookDirection = -dir * _orbitDistance;
        MeshCamera.NearPlaneDistance = Math.Max(1e-5, _orbitDistance * 0.01);
        MeshCamera.FarPlaneDistance = _orbitDistance + _orbitRadius * 8;
        // Headlight shines from just above the camera.
        _headLight.Direction = -dir + new Vector3D(0, -0.35, 0);
    }

    private void MeshPanel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnAnimationBar(e.OriginalSource)) return;
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            ResetOrbit();
            return;
        }
        _dragButton = e.ChangedButton;
        _lastMouse = e.GetPosition(MeshPanel);
        MeshPanel.CaptureMouse();
    }

    private void MeshPanel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragButton != e.ChangedButton) return;
        _dragButton = null;
        MeshPanel.ReleaseMouseCapture();
    }

    private void MeshPanel_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragButton == null) return;
        var p = e.GetPosition(MeshPanel);
        var d = p - _lastMouse;
        _lastMouse = p;

        if (_dragButton == MouseButton.Left)
        {
            _orbitYaw -= d.X * 0.01;
            _orbitPitch = Math.Clamp(_orbitPitch + d.Y * 0.01, -1.55, 1.55);
        }
        else
        {
            // Pan in the camera plane, scaled so the model follows the mouse.
            var forward = MeshCamera.LookDirection; forward.Normalize();
            var right = Vector3D.CrossProduct(forward, new Vector3D(0, 1, 0)); right.Normalize();
            var up = Vector3D.CrossProduct(right, forward);
            double scale = 2 * _orbitDistance * Math.Tan(MeshCamera.FieldOfView / 2 * Math.PI / 180) / Math.Max(1, MeshPanel.ActualWidth);
            _orbitTarget += (-right * d.X + up * d.Y) * scale;
        }
        UpdateCamera();
    }

    private void MeshPanel_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsOnAnimationBar(e.OriginalSource)) return;
        _orbitDistance = Math.Clamp(_orbitDistance * (e.Delta > 0 ? 0.85 : 1 / 0.85), _orbitRadius * 0.05, _orbitRadius * 200);
        UpdateCamera();
        e.Handled = true;
    }

    private void MeshResetView_Click(object sender, RoutedEventArgs e) => ResetOrbit();

    private void MeshSubmeshColours_Click(object sender, RoutedEventArgs e) => BuildScene();
}
