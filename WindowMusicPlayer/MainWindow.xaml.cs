using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Forms;

namespace WindowMusicPlayer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Track> _allTracks = new();
    private readonly ObservableCollection<Track> _displayedTracks = new();
    private readonly Dictionary<string, List<Track>> _playlists = new();
    private readonly MediaPlayer _mediaPlayer = new();
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Queue<Track> _shuffleQueue = new();
    private string _rootFolder = string.Empty;
    private Track? _currentTrack;
    private bool _isSeeking;
    private bool _loopSingle;

    public MainWindow()
    {
        InitializeComponent();
        SongsListView.ItemsSource = _displayedTracks;
        _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
        _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        _positionTimer.Tick += PositionTimer_Tick;
        _mediaPlayer.Volume = 1.0;
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog();
        dialog.Description = "Chọn thư mục chứa nhạc";
        dialog.ShowNewFolderButton = false;

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        _rootFolder = dialog.SelectedPath;
        LoadMusicFromFolder(_rootFolder);
    }

    private void LoadMusicFromFolder(string rootPath)
    {
        _allTracks.Clear();
        _playlists.Clear();
        PlaylistCombo.Items.Clear();

        var supportedExtensions = new[] { ".mp3", ".wav", ".wma", ".aac", ".m4a" };
        var files = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
            .Where(file => supportedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()));

        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file) ?? rootPath;
            var playlistName = string.IsNullOrWhiteSpace(directory)
                ? "Root"
                : Path.GetRelativePath(rootPath, directory).Replace(Path.DirectorySeparatorChar, '/');

            if (string.IsNullOrWhiteSpace(playlistName))
                playlistName = "Root";

            var track = new Track
            {
                FilePath = file,
                Name = Path.GetFileNameWithoutExtension(file),
                Playlist = playlistName,
            };

            _allTracks.Add(track);

            if (!_playlists.TryGetValue(playlistName, out var playlistTracks))
            {
                playlistTracks = new List<Track>();
                _playlists[playlistName] = playlistTracks;
            }

            playlistTracks.Add(track);
        }

        _displayedTracks.Clear();
        foreach (var track in _allTracks)
            _displayedTracks.Add(track);

        PlaylistCombo.Items.Add("All songs");
        foreach (var playlist in _playlists.Keys.OrderBy(name => name))
            PlaylistCombo.Items.Add(playlist);

        PlaylistCombo.SelectedIndex = 0;
        TxtStatus.Text = $"Loaded {_allTracks.Count} tracks from: {rootPath}";
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        var hasTracks = _allTracks.Count > 0;
        BtnShuffleAll.IsEnabled = hasTracks;
        BtnShufflePlaylist.IsEnabled = hasTracks;
        BtnPlay.IsEnabled = hasTracks;
        BtnPause.IsEnabled = hasTracks;
        BtnStop.IsEnabled = hasTracks;
        BtnNext.IsEnabled = _shuffleQueue.Any();
        BtnLoopOne.IsEnabled = hasTracks;
        UpdateLoopButtonStyle();
    }

    private void PlaylistCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistCombo.SelectedItem is not string selected)
            return;

        _displayedTracks.Clear();

        if (selected == "All songs")
        {
            foreach (var track in _allTracks)
                _displayedTracks.Add(track);
        }
        else if (_playlists.TryGetValue(selected, out var playlistTracks))
        {
            foreach (var track in playlistTracks)
                _displayedTracks.Add(track);
        }

        TxtStatus.Text = selected == "All songs"
            ? $"Showing all {_displayedTracks.Count} tracks."
            : $"Showing {_displayedTracks.Count} tracks in playlist: {selected}.";
    }

    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SongsListView.SelectedItem is Track selectedTrack)
        {
            _currentTrack = selectedTrack;
            TxtCurrentTrack.Text = selectedTrack.Name;
        }
    }

    private void SongsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SongsListView.SelectedItem is Track selectedTrack)
        {
            _currentTrack = selectedTrack;
            PlayTrack(selectedTrack);
        }
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTrack is null)
        {
            if (_displayedTracks.Count == 0)
                return;

            _currentTrack = _displayedTracks.First();
        }

        PlayTrack(_currentTrack);
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.Pause();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.Stop();
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        PlayNextFromQueue();
    }

    private void BtnLoopOne_Click(object sender, RoutedEventArgs e)
    {
        _loopSingle = !_loopSingle;
        UpdateLoopButtonStyle();
        TxtStatus.Text = _loopSingle ? "Loop single track enabled." : "Loop single track disabled.";
    }

    private void UpdateLoopButtonStyle()
    {
        if (BtnLoopOne is null)
            return;

        BtnLoopOne.Background = _loopSingle
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(64, 153, 255))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 31, 31));
    }


    private void BtnShuffleAll_Click(object sender, RoutedEventArgs e)
    {
        if (_allTracks.Count == 0)
            return;

        StartShuffle(_allTracks);
        TxtStatus.Text = $"Shuffle All started with {_shuffleQueue.Count + 1} tracks.";
    }

    private void BtnShufflePlaylist_Click(object sender, RoutedEventArgs e)
    {
        IEnumerable<Track> targets;
        if (PlaylistCombo.SelectedItem is string selected && selected != "All songs" && _playlists.TryGetValue(selected, out var playlistTracks))
        {
            targets = playlistTracks;
        }
        else if (_currentTrack is not null && _playlists.TryGetValue(_currentTrack.Playlist, out var samePlaylist))
        {
            targets = samePlaylist;
        }
        else
        {
            System.Windows.MessageBox.Show("Vui lòng chọn playlist hoặc chọn một bài trong playlist để shuffle.", "Shuffle Playlist", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StartShuffle(targets);
        TxtStatus.Text = $"Shuffle Playlist started for '{targets.First().Playlist}' with {_shuffleQueue.Count + 1} tracks.";
    }

    private void StartShuffle(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        if (list.Count == 0)
            return;

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
            TxtQueueInfo.Text = "Queue: 0 / 0";
            BtnNext.IsEnabled = false;
            return;
        }

        _currentTrack = _shuffleQueue.Dequeue();
        PlayTrack(_currentTrack);
        UpdateQueueInfo();
    }

    private void PlayTrack(Track track)
    {
        try
        {
            _mediaPlayer.Open(new Uri(track.FilePath));
            _mediaPlayer.Play();
            TxtCurrentTrack.Text = track.Name;
            _currentTrack = track;
            UpdateQueueInfo();
            BtnNext.IsEnabled = _shuffleQueue.Any();
        }
        catch
        {
            System.Windows.MessageBox.Show($"Không thể phát bài hát: {track.Name}", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateQueueInfo()
    {
        var total = _shuffleQueue.Count + (_currentTrack is null ? 0 : 1);
        var remaining = _shuffleQueue.Count;
        TxtQueueInfo.Text = $"Queue: {remaining} remaining / {total} total";
    }

    private void MediaPlayer_MediaOpened(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                var duration = _mediaPlayer.NaturalDuration.TimeSpan;
                ProgressSlider.Maximum = duration.TotalSeconds;
                TxtDuration.Text = $"/ {duration:mm\\:ss}";
                ProgressSlider.IsEnabled = true;
                _positionTimer.Start();
            }
        });
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_isSeeking)
            return;

        if (_mediaPlayer.NaturalDuration.HasTimeSpan)
        {
            var position = _mediaPlayer.Position;
            ProgressSlider.Value = position.TotalSeconds;
            TxtPosition.Text = position.ToString(@"mm\:ss");
        }
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking)
        {
            TxtPosition.Text = TimeSpan.FromSeconds(ProgressSlider.Value).ToString("mm\\:ss");
        }
    }


    private void ProgressSlider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
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
            if (_loopSingle && _currentTrack is not null)
            {
                PlayTrack(_currentTrack);
            }
            else
            {
                PlayNextFromQueue();
            }
        });
    }

    private void MediaPlayer_MediaFailed(object? sender, ExceptionEventArgs e)
    {
        Dispatcher.Invoke(() => System.Windows.MessageBox.Show($"Phát nhạc thất bại: {e.ErrorException?.Message}", "Media Error", MessageBoxButton.OK, MessageBoxImage.Warning));
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
}

