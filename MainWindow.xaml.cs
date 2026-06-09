using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Forms;
using Wpf.Ui.Controls;

namespace WindowMusicPlayer;

public partial class MainWindow : FluentWindow
{
    private readonly ObservableCollection<Track> _allTracks = new();
    private readonly ObservableCollection<Track> _displayedTracks = new();
    private readonly Dictionary<string, List<Track>> _playlists = new();
    private readonly MediaPlayer _mediaPlayer = new();
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Stack<Track> _history = new();
    private Queue<Track> _shuffleQueue = new();
    private HwndSource? _hwndSource;
    private Track? _currentTrack;
    private bool _isSeeking;
    private bool _isPlaying;
    private bool _loopSingle;

    private const int HotkeyPrev = 1;
    private const int HotkeyPlay = 2;
    private const int HotkeyNext = 3;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkMediaPrev = 0xB1;
    private const uint VkMediaPlayPause = 0xB3;
    private const uint VkMediaNext = 0xB0;
    private const int WmHotkey = 0x0312;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WindowMusicPlayer", "lastfolder.txt");

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            WindowState = System.Windows.WindowState.Maximized;
            var handle = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(WndProc);
            RegisterHotKey(handle, HotkeyPrev, ModNoRepeat, VkMediaPrev);
            RegisterHotKey(handle, HotkeyPlay, ModNoRepeat, VkMediaPlayPause);
            RegisterHotKey(handle, HotkeyNext, ModNoRepeat, VkMediaNext);
        };
        PreviewKeyDown += Window_PreviewKeyDown;
        ApplyWindowIcon();
        SongsListView.ItemsSource = _displayedTracks;
        _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
        _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        _positionTimer.Tick += PositionTimer_Tick;
        _mediaPlayer.Volume = 1.0;
        Loaded += (_, _) =>
        {
            var last = LoadLastFolder();
            if (!string.IsNullOrEmpty(last) && Directory.Exists(last))
                LoadMusicFromFolder(last);
        };
    }

    private void ApplyWindowIcon()
    {
        if (Resources["AppIcon"] is not DrawingImage drawing) return;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawImage(drawing, new Rect(0, 0, 64, 64));
        var bmp = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        Icon = bmp;
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog { ShowNewFolderButton = false };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        LoadMusicFromFolder(dialog.SelectedPath);
    }

    private void LoadMusicFromFolder(string rootPath)
    {
        _allTracks.Clear();
        _playlists.Clear();
        PlaylistCombo.Items.Clear();

        var extensions = new[] { ".mp3", ".wav", ".wma", ".aac", ".m4a" };
        foreach (var file in Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
        {
            var rel = Path.GetRelativePath(rootPath, Path.GetDirectoryName(file) ?? rootPath);
            var playlistName = rel is "." or "" ? "Root" : rel.Replace(Path.DirectorySeparatorChar, '/');

            _allTracks.Add(new Track { FilePath = file, Name = Path.GetFileNameWithoutExtension(file), Playlist = playlistName });

            if (!_playlists.TryGetValue(playlistName, out var list))
                _playlists[playlistName] = list = new List<Track>();
            list.Add(_allTracks[^1]);
        }

        _displayedTracks.Clear();
        foreach (var t in _allTracks) _displayedTracks.Add(t);

        PlaylistCombo.Items.Add("All songs");
        foreach (var name in _playlists.Keys.OrderBy(n => n)) PlaylistCombo.Items.Add(name);

        PlaylistCombo.SelectedIndex = 0;
        TxtStatus.Text = $"Loaded {_allTracks.Count} tracks from: {rootPath}";
        UpdateUiState();
        SaveLastFolder(rootPath);
    }

    private void UpdateUiState()
    {
        var hasTracks = _allTracks.Count > 0;
        BtnShuffleAll.IsEnabled = hasTracks;
        BtnShufflePlaylist.IsEnabled = hasTracks;
        UpdateLoopButtonStyle();
        UpdatePlayPauseIcon();
    }

    private void PlaylistCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistCombo.SelectedItem is not string selected) return;

        _displayedTracks.Clear();
        var source = selected == "All songs" ? _allTracks
            : _playlists.TryGetValue(selected, out var pl) ? pl : Enumerable.Empty<Track>();
        foreach (var t in source) _displayedTracks.Add(t);

        TxtStatus.Text = selected == "All songs"
            ? $"Showing all {_displayedTracks.Count} tracks."
            : $"Showing {_displayedTracks.Count} tracks in playlist: {selected}.";
    }

    private void SongsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SongsListView.SelectedItem is Track t) PlayTrack(t);
    }

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            _mediaPlayer.Pause();
            _isPlaying = false;
            TxtStatus.Text = _currentTrack is not null ? $"Paused: {_currentTrack.Name}" : "Paused";
            UpdatePlayPauseIcon();
            return;
        }

        if (_currentTrack is not null)
        {
            if (SongsListView.SelectedItem is Track selected && selected != _currentTrack)
                PlayTrack(selected);
            else
            {
                _mediaPlayer.Play();
                _isPlaying = true;
                TxtStatus.Text = $"Resumed: {_currentTrack.Name}";
                UpdatePlayPauseIcon();
            }
            return;
        }

        var track = SongsListView.SelectedItem as Track ?? _displayedTracks.FirstOrDefault();
        if (track is not null) PlayTrack(track);
    }

    private void BtnPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTrack is not null
            && _mediaPlayer.NaturalDuration.HasTimeSpan
            && _mediaPlayer.Position.TotalSeconds > 3)
        {
            _mediaPlayer.Position = TimeSpan.Zero;
            return;
        }

        if (_history.TryPop(out var prev))
            PlayTrack(prev, addToHistory: false);
        else if (_currentTrack is not null)
            _mediaPlayer.Position = TimeSpan.Zero;
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e) => PlayNextFromQueue();

    private void BtnLoopOne_Click(object sender, RoutedEventArgs e)
    {
        _loopSingle = !_loopSingle;
        UpdateLoopButtonStyle();
        TxtStatus.Text = _loopSingle ? "Loop single track enabled." : "Loop single track disabled.";
    }

    private void UpdateLoopButtonStyle()
    {
        if (BtnLoopOne is null) return;
        var purple = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x68, 0x00, 0x70));
        BtnLoopOne.Background = _loopSingle ? purple : System.Windows.Media.Brushes.Transparent;
        BtnLoopOne.Foreground = _loopSingle ? System.Windows.Media.Brushes.White : purple;
    }

    private void UpdatePlayPauseIcon()
    {
        if (PlayPauseIcon is null) return;
        PlayPauseIcon.Symbol = _isPlaying ? SymbolRegular.Pause20 : SymbolRegular.Play20;
    }

    private void BtnShuffleAll_Click(object sender, RoutedEventArgs e)
    {
        if (_allTracks.Count == 0) return;
        StartShuffle(_allTracks);
        TxtStatus.Text = $"Shuffle All started with {_shuffleQueue.Count + 1} tracks.";
    }

    private void BtnShufflePlaylist_Click(object sender, RoutedEventArgs e)
    {
        IEnumerable<Track> targets;
        if (PlaylistCombo.SelectedItem is string sel && sel != "All songs"
            && _playlists.TryGetValue(sel, out var pl))
            targets = pl;
        else if (_currentTrack is not null && _playlists.TryGetValue(_currentTrack.Playlist, out var same))
            targets = same;
        else
        {
            System.Windows.MessageBox.Show(
                "Vui lòng chọn playlist hoặc chọn một bài trong playlist để shuffle.",
                "Shuffle Playlist", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        StartShuffle(targets);
        TxtStatus.Text = $"Shuffle Playlist started for '{targets.First().Playlist}' with {_shuffleQueue.Count + 1} tracks.";
    }

    private void StartShuffle(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        if (list.Count == 0) return;
        Shuffle(list);
        _shuffleQueue = new Queue<Track>(list);
        PlayNextFromQueue();
        UpdateUiState();
    }

    private void PlayNextFromQueue()
    {
        if (_shuffleQueue.Count == 0)
        {
            _currentTrack = null;
            _isPlaying = false;
            TxtQueueInfo.Text = "Queue: 0 / 0";
            UpdatePlayPauseIcon();
            return;
        }
        PlayTrack(_shuffleQueue.Dequeue());
        UpdateQueueInfo();
    }

    private void PlayTrack(Track track, bool addToHistory = true)
    {
        try
        {
            if (addToHistory && _currentTrack is not null && _currentTrack != track)
                _history.Push(_currentTrack);
            _mediaPlayer.Open(new Uri(track.FilePath));
            _mediaPlayer.Play();
            _isPlaying = true;
            TxtCurrentTrack.Text = track.Name;
            _currentTrack = track;
            UpdateQueueInfo();
            UpdatePlayPauseIcon();
        }
        catch
        {
            System.Windows.MessageBox.Show(
                $"Không thể phát bài hát: {track.Name}", "Playback Error",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void UpdateQueueInfo()
    {
        var total = _shuffleQueue.Count + (_currentTrack is null ? 0 : 1);
        TxtQueueInfo.Text = $"Queue: {_shuffleQueue.Count} remaining / {total} total";
    }

    private void MediaPlayer_MediaOpened(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_mediaPlayer.NaturalDuration.HasTimeSpan) return;
            var duration = _mediaPlayer.NaturalDuration.TimeSpan;
            ProgressSlider.Maximum = duration.TotalSeconds;
            TxtDuration.Text = $"{duration:mm\\:ss}";
            ProgressSlider.IsEnabled = true;
            _positionTimer.Start();
        });
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_isSeeking || !_mediaPlayer.NaturalDuration.HasTimeSpan) return;
        var pos = _mediaPlayer.Position;
        ProgressSlider.Value = pos.TotalSeconds;
        TxtPosition.Text = pos.ToString(@"mm\:ss");
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking)
            TxtPosition.Text = TimeSpan.FromSeconds(ProgressSlider.Value).ToString(@"mm\:ss");
    }

    private void ProgressSlider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider)
        {
            var ratio = slider.ActualWidth <= 0 ? 0.0 : e.GetPosition(slider).X / slider.ActualWidth;
            slider.Value = Math.Clamp(slider.Minimum + ratio * (slider.Maximum - slider.Minimum),
                slider.Minimum, slider.Maximum);
        }
        _isSeeking = true;
    }

    private void ProgressSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        _mediaPlayer.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
    }

    private void MediaPlayer_MediaEnded(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _isPlaying = false;
            if (_loopSingle && _currentTrack is not null) PlayTrack(_currentTrack);
            else PlayNextFromQueue();
        });
    }

    private void MediaPlayer_MediaFailed(object? sender, ExceptionEventArgs e)
    {
        Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
            $"Phát nhạc thất bại: {e.ErrorException?.Message}", "Media Error",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning));
    }

    private static void Shuffle<T>(IList<T> list)
    {
        var rng = new Random();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case HotkeyPrev: BtnPrevious_Click(this, new RoutedEventArgs()); break;
            case HotkeyPlay: BtnPlayPause_Click(this, new RoutedEventArgs()); break;
            case HotkeyNext: BtnNext_Click(this, new RoutedEventArgs()); break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: BtnPlayPause_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.Left:  BtnPrevious_Click(this, new RoutedEventArgs());  e.Handled = true; break;
            case Key.Right: BtnNext_Click(this, new RoutedEventArgs());      e.Handled = true; break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(handle, HotkeyPrev);
        UnregisterHotKey(handle, HotkeyPlay);
        UnregisterHotKey(handle, HotkeyNext);
        _hwndSource?.RemoveHook(WndProc);
        base.OnClosed(e);
    }

    private static void SaveLastFolder(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, path);
    }

    private static string? LoadLastFolder()
    {
        try { return File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath).Trim() : null; }
        catch { return null; }
    }
}
