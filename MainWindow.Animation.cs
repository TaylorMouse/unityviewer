using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using UnityBrowser.Unity;

namespace UnityBrowser;

/// <summary>AnimationClip preview: the clip plays on its character in the 3D viewer, with a playback bar.</summary>
public partial class MainWindow
{
    private ClipPlayer? _clipPlayer;
    private List<(ClipPlayer.Skin Skin, MeshGeometry3D[] Parts)>? _animParts;
    private readonly Stopwatch _animClock = new();
    private double _animTime, _animClockBase;
    private bool _animPlaying, _updatingAnimSlider;

    private async Task ShowAnimationAsync(TreeNode node, ObjectRef obj)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        StopAudio();
        HideMesh();
        EnsurePreviewVisible();
        PreviewToolbar.Visibility = Visibility.Collapsed;
        PreviewScroll.Visibility = Visibility.Visible;
        ShowPreviewMessage("Loading animation...");

        try
        {
            // Build the player and find the bounds of the whole motion off the UI thread.
            var (player, min, max) = await Task.Run(() =>
            {
                var p = ClipPlayer.Create(obj.File, obj.Info);
                double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
                for (int f = 0; f < 12; f++)
                {
                    p.Evaluate(p.StartTime + p.Duration * f / 11);
                    foreach (var skin in p.Skins)
                        for (int i = 0; i < skin.Positions.Length; i++)
                        {
                            lo[i % 3] = Math.Min(lo[i % 3], skin.Positions[i]);
                            hi[i % 3] = Math.Max(hi[i % 3], skin.Positions[i]);
                        }
                }
                p.Evaluate(p.StartTime);
                return (p, lo, hi);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;

            // Geometry lives on the UI thread; positions and normals are replaced every frame.
            _clipPlayer = player;
            _animParts = player.Skins.Select(skin => (skin, skin.Mesh.SubMeshes.Where(s => s.Triangles.Length > 0)
                .Select(s =>
                {
                    var indices = new Int32Collection(s.Triangles);
                    indices.Freeze();
                    return new MeshGeometry3D { TriangleIndices = indices };
                }).ToArray())).ToList();
            _meshParts = _animParts.SelectMany(a => a.Parts).ToArray();
            ApplyAnimationFrame();
            BuildScene();

            PreviewMessage.Visibility = Visibility.Collapsed;
            PreviewScroll.Visibility = Visibility.Collapsed;
            MeshPanel.Visibility = Visibility.Visible;
            MeshToolbar.Visibility = Visibility.Visible;
            AnimationBar.Visibility = Visibility.Visible;
            MeshHint.Margin = new Thickness(10, 0, 0, 50);
            MeshPanel.UpdateLayout();
            FrameBounds(min, max);

            _updatingAnimSlider = true;
            AnimSlider.Maximum = Math.Max(0.001, player.Duration);
            AnimSlider.Value = 0;
            _updatingAnimSlider = false;
            _animTime = 0;
            SetAnimationPlaying(true);

            var clip = player.Clip;
            int keys = player.Layered.Layers.Sum(l => l.Clip.Curves.Sum(c => c.Keys.Count));
            var info = new List<PropertyRow>();
            if (player.Layered.IsLayered)
                info.Add(new("Layered", string.Join(" + ", player.Layered.Layers.Select(l => $"{l.Clip.Name} ({l.Role})")) +
                                        ", combined as the game plays them"));
            else if (player.Layered.MissingDependency is { } missing)
                info.Add(new("Partial clip", $"This is an override layer; its base animation (the body motion) is in {missing}, " +
                                             "which is not open. Use File > Find Dependencies."));
            info.AddRange(new List<PropertyRow>
            {
                new("Duration", $"{player.Duration:0.00} s ({Math.Round(player.Duration * clip.SampleRate) + 1:0} frames at {clip.SampleRate:0.##} fps)"),
                new("Animated bones", player.AnimatedTransforms.ToString("N0")),
                new("Keyframes", $"{keys:N0} on {player.Layered.Layers.Sum(l => l.Clip.Curves.Length):N0} curves"),
                new("Plays on", string.Join(", ", player.Skins.Select(s => $"{s.Mesh.Name} ({s.Mesh.VertexCount:N0} vertices)"))),
            });
            PropertyList.ItemsSource = info.Concat(node.Properties).ToList();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) ShowPreviewMessage($"Can't play this AnimationClip.\n{ex.Message}");
        }
    }

    /// <summary>Stops playback and releases the player (called whenever the mesh view is hidden).</summary>
    private void StopAnimation()
    {
        SetAnimationPlaying(false);
        _clipPlayer = null;
        _animParts = null;
        AnimationBar.Visibility = Visibility.Collapsed;
        MeshHint.Margin = new Thickness(10, 0, 0, 8);
    }

    private void SetAnimationPlaying(bool playing)
    {
        if (playing && _clipPlayer == null) return;
        if (playing == _animPlaying) return;
        _animPlaying = playing;
        AnimPlayButton.Content = playing ? "" : ""; // Pause / Play glyphs
        if (playing)
        {
            if (_clipPlayer != null && _animTime >= _clipPlayer.Duration) _animTime = 0; // replay from the start
            _animClockBase = _animTime;
            _animClock.Restart();
            CompositionTarget.Rendering += OnAnimationFrame;
        }
        else
        {
            _animClock.Stop();
            CompositionTarget.Rendering -= OnAnimationFrame;
        }
    }

    private void ToggleAnimation() => SetAnimationPlaying(!_animPlaying);

    private void OnAnimationFrame(object? sender, EventArgs e)
    {
        if (_clipPlayer == null) return;
        double duration = _clipPlayer.Duration;
        double t = _animClockBase + _animClock.Elapsed.TotalSeconds;
        if (t > duration)
        {
            if (AnimLoopBox.IsChecked == true && duration > 0)
            {
                t %= duration;
                _animClockBase = t;
                _animClock.Restart();
            }
            else
            {
                t = duration;
                _animTime = t;
                ApplyAnimationFrame();
                SetAnimationPlaying(false);
                return;
            }
        }
        _animTime = t;
        ApplyAnimationFrame();
    }

    /// <summary>Evaluates the clip at the current time and pushes the deformed vertices to the viewport.</summary>
    private void ApplyAnimationFrame()
    {
        if (_clipPlayer == null || _animParts == null) return;
        _clipPlayer.Evaluate(_clipPlayer.StartTime + (float)_animTime);
        foreach (var (skin, parts) in _animParts)
        {
            var p = skin.Positions;
            var positions = new Point3DCollection(p.Length / 3);
            for (int i = 0; i < p.Length; i += 3) positions.Add(new Point3D(p[i], p[i + 1], p[i + 2]));
            positions.Freeze();
            Vector3DCollection? normals = null;
            if (skin.Normals is { } n)
            {
                normals = new Vector3DCollection(n.Length / 3);
                for (int i = 0; i < n.Length; i += 3) normals.Add(new Vector3D(n[i], n[i + 1], n[i + 2]));
                normals.Freeze();
            }
            foreach (var part in parts)
            {
                part.Positions = positions;
                if (normals != null) part.Normals = normals;
            }
        }

        _updatingAnimSlider = true;
        AnimSlider.Value = _animTime;
        _updatingAnimSlider = false;
        double rate = _clipPlayer.Clip.SampleRate;
        AnimTimeText.Text = $"{_animTime:0.00} / {_clipPlayer.Duration:0.00} s   frame {Math.Round(_animTime * rate):0}";
    }

    private void AnimPlay_Click(object sender, RoutedEventArgs e) => ToggleAnimation();

    private void AnimSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingAnimSlider || _clipPlayer == null) return;
        _animTime = e.NewValue;
        _animClockBase = _animTime;
        _animClock.Restart();
        if (!_animPlaying) ApplyAnimationFrame();
    }

    /// <summary>Clicks and drags on the playback bar must not orbit the camera.</summary>
    private bool IsOnAnimationBar(object source)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, AnimationBar)) return true;
        return false;
    }
}
