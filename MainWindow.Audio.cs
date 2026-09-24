using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnityBrowser.Audio;

namespace UnityBrowser;

/// <summary>AudioClip playback: decode to a temporary WAV and play it with WPF's MediaPlayer.</summary>
public partial class MainWindow
{
    private static readonly string AudioTempFolder = Path.Combine(Path.GetTempPath(), "UnityBrowser");

    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _audioTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private string? _audioFile;
    private TimeSpan _audioDuration;
    private bool _isPlaying;
    private bool _updatingSlider;

    private void InitAudio()
    {
        CleanAudioTemp();
        _player.Volume = VolumeSlider.Value;
        _player.MediaEnded += (_, _) =>
        {
            _player.Pause();
            _player.Position = TimeSpan.Zero;
            SetPlaying(false);
            UpdateAudioPosition();
        };
        _player.MediaFailed += (_, e) => ShowPreviewMessage($"Can't play this audio.\n{e.ErrorException?.Message}");
        _audioTimer.Tick += (_, _) => UpdateAudioPosition();
    }

    private async Task ShowAudioAsync(TreeNode node, ObjectRef obj)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        StopAudio();
        HideMesh();
        EnsurePreviewVisible();
        PreviewToolbar.Visibility = Visibility.Collapsed;
        PreviewScroll.Visibility = Visibility.Visible;
        ShowPreviewMessage("Decoding audio...");

        try
        {
            var (audio, waveform, path) = await Task.Run(() =>
            {
                var a = AudioClipDecoder.Decode(obj.File, obj.Info);
                Directory.CreateDirectory(AudioTempFolder);
                string file = Path.Combine(AudioTempFolder, $"{Guid.NewGuid():N}.wav");
                WavWriter.Write(file, a);
                return (a, BuildWaveform(a, 1600, 150), file);
            }, cts.Token);

            if (cts.IsCancellationRequested)
            {
                TryDelete(path);
                return;
            }

            _audioFile = path;
            _audioDuration = audio.Duration;
            WaveformImage.Source = waveform;
            _updatingSlider = true;
            PositionSlider.Maximum = Math.Max(0.001, _audioDuration.TotalSeconds);
            PositionSlider.Value = 0;
            _updatingSlider = false;

            PreviewMessage.Visibility = Visibility.Collapsed;
            AudioPanel.Visibility = Visibility.Visible;

            _player.Open(new Uri(path));
            _player.Play();
            SetPlaying(true);
            _audioTimer.Start();
            UpdateAudioPosition();

            PropertyList.ItemsSource = audio.Info.Select(i => new PropertyRow(i.Key, i.Value))
                .Concat(node.Properties).ToList();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) ShowPreviewMessage($"Can't play this AudioClip.\n{ex.Message}");
        }
    }

    /// <summary>Stops playback, hides the player and removes the temporary WAV.</summary>
    private void StopAudio()
    {
        _audioTimer.Stop();
        _player.Stop();
        _player.Close();
        SetPlaying(false);
        AudioPanel.Visibility = Visibility.Collapsed;
        WaveformImage.Source = null;
        if (_audioFile != null)
        {
            TryDelete(_audioFile);
            _audioFile = null;
        }
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        PlayPauseButton.Content = playing ? "" : ""; // Pause / Play glyphs
    }

    private void TogglePlayPause()
    {
        if (_audioFile == null) return;
        if (_isPlaying)
        {
            _player.Pause();
            SetPlaying(false);
        }
        else
        {
            _player.Play();
            SetPlaying(true);
            _audioTimer.Start();
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void StopAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_audioFile == null) return;
        _player.Pause();
        _player.Position = TimeSpan.Zero;
        SetPlaying(false);
        UpdateAudioPosition();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        _player.Volume = e.NewValue;

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || _audioFile == null) return;
        _player.Position = TimeSpan.FromSeconds(e.NewValue);
        UpdateAudioPosition();
    }

    private void Waveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_audioFile == null || WaveformHost.ActualWidth <= 0) return;
        double fraction = Math.Clamp(e.GetPosition(WaveformHost).X / WaveformHost.ActualWidth, 0, 1);
        _player.Position = TimeSpan.FromSeconds(fraction * _audioDuration.TotalSeconds);
        UpdateAudioPosition();
    }

    private void UpdateAudioPosition()
    {
        var pos = _player.Position;
        if (pos > _audioDuration) pos = _audioDuration;
        _updatingSlider = true;
        PositionSlider.Value = pos.TotalSeconds;
        _updatingSlider = false;

        AudioTimeText.Text = $"{AudioClipDecoder.FormatTime(pos)} / {AudioClipDecoder.FormatTime(_audioDuration)}";
        double fraction = _audioDuration.TotalSeconds > 0 ? pos.TotalSeconds / _audioDuration.TotalSeconds : 0;
        double x = fraction * WaveformHost.ActualWidth;
        PlayedOverlay.Width = Math.Max(0, x);
        Playhead.Margin = new Thickness(Math.Max(0, x - 1), 0, 0, 0);
    }

    /// <summary>Peak waveform (min/max per column of the mono mix), drawn into a frozen bitmap.</summary>
    private static BitmapSource BuildWaveform(DecodedAudio audio, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        int frames = audio.Samples.Length / audio.Channels;
        int mid = height / 2;
        (byte b, byte g, byte r) wave = (0xF7, 0xC3, 0x4F); // #4FC3F7

        for (int x = 0; x < width; x++)
        {
            long start = (long)frames * x / width, end = (long)frames * (x + 1) / width;
            float min = 0, max = 0;
            for (long f = start; f < end; f++)
            {
                float v = 0;
                for (int c = 0; c < audio.Channels; c++) v += audio.Samples[f * audio.Channels + c];
                v /= audio.Channels * 32768f;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            int top = Math.Clamp(mid - (int)(max * (mid - 2)), 0, height - 1);
            int bottom = Math.Clamp(mid - (int)(min * (mid - 2)), 0, height - 1);
            for (int y = top; y <= bottom; y++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = wave.b; pixels[i + 1] = wave.g; pixels[i + 2] = wave.r; pixels[i + 3] = 255;
            }
        }
        // Centre line.
        for (int x = 0; x < width; x++)
        {
            int i = (mid * width + x) * 4;
            if (pixels[i + 3] == 0) { pixels[i] = pixels[i + 1] = pixels[i + 2] = 0x60; pixels[i + 3] = 255; }
        }

        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bmp.Freeze();
        return bmp;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* still locked or already gone */ }
    }

    private static void CleanAudioTemp()
    {
        try
        {
            if (!Directory.Exists(AudioTempFolder)) return;
            foreach (var f in Directory.EnumerateFiles(AudioTempFolder, "*.wav")) TryDelete(f);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }
}
