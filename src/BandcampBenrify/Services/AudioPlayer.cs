using System.IO;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace BandcampBenrify.Services;

public sealed record NowPlayingInfo(string Title, string Artist, string Album, string? ArtUrl);

/// <summary>
/// WinRT の MediaPlayer (Media Foundation) で mp3 ストリームを再生する。
/// Windows のメディアキー / 音量オーバーレイ (System Media Transport Controls) にも曲名とジャケットを出す。
/// イベントは UI スレッドに戻してから発火する。
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly MediaPlayer _player;
    private readonly Dispatcher _dispatcher;

    public AudioPlayer(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _player = new MediaPlayer
        {
            AutoPlay = false,
            AudioCategory = MediaPlayerAudioCategory.Media,
        };

        _player.MediaOpened += (_, _) => Post(() => Opened?.Invoke());
        _player.MediaEnded += (_, _) => Post(() => Ended?.Invoke());
        _player.MediaFailed += (_, e) => Post(() => Failed?.Invoke($"{e.Error}: {e.ErrorMessage}".Trim(' ', ':')));
        _player.PlaybackSession.PlaybackStateChanged += (s, _) =>
        {
            var playing = s.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Opening or MediaPlaybackState.Buffering;
            Post(() => PlayingChanged?.Invoke(playing));
        };

        // キーボードのメディアキー / Windows のメディアコントロールからの「次へ」「前へ」
        var commands = _player.CommandManager;
        commands.IsEnabled = true;
        commands.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        commands.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        commands.NextReceived += (_, e) =>
        {
            e.Handled = true;
            Post(() => NextRequested?.Invoke());
        };
        commands.PreviousReceived += (_, e) =>
        {
            e.Handled = true;
            Post(() => PreviousRequested?.Invoke());
        };
    }

    public event Action? Opened;

    public event Action? Ended;

    public event Action<string>? Failed;

    /// <summary>メディアキーなど外部から再生 / 一時停止された場合も含めて通知する。</summary>
    public event Action<bool>? PlayingChanged;

    public event Action? NextRequested;

    public event Action? PreviousRequested;

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 1);
    }

    public bool IsMuted
    {
        get => _player.IsMuted;
        set => _player.IsMuted = value;
    }

    /// <summary>1 曲リピート (曲の最後まで行ったら頭から再生し、Ended は発生しない)。</summary>
    public bool IsLooping
    {
        get => _player.IsLoopingEnabled;
        set => _player.IsLoopingEnabled = value;
    }

    public TimeSpan Position
    {
        get => _player.PlaybackSession.Position;
        set
        {
            if (_player.PlaybackSession.CanSeek)
            {
                _player.PlaybackSession.Position = value;
            }
        }
    }

    public TimeSpan? Duration => _player.PlaybackSession.NaturalDuration is { Ticks: > 0 } d ? d : null;

    public void Open(string url, NowPlayingInfo info) => Open(MediaSource.CreateFromUri(new Uri(url)), info);

    /// <summary>ダウンロード中のデータを読むストリームから再生する (まだ届いていない位置は届くまで待つ)。</summary>
    public void Open(Stream mp3, NowPlayingInfo info) =>
        Open(MediaSource.CreateFromStream(mp3.AsRandomAccessStream(), "audio/mpeg"), info);

    private void Open(MediaSource source, NowPlayingInfo info)
    {
        var item = new MediaPlaybackItem(source);
        var props = item.GetDisplayProperties();
        props.Type = MediaPlaybackType.Music;
        props.MusicProperties.Title = info.Title;
        props.MusicProperties.Artist = info.Artist;
        props.MusicProperties.AlbumTitle = info.Album;
        if (!string.IsNullOrEmpty(info.ArtUrl))
        {
            props.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(info.ArtUrl));
        }

        item.ApplyDisplayProperties(props);
        _player.Source = item;
    }

    public void Play() => _player.Play();

    public void Pause() => _player.Pause();

    public void Stop()
    {
        _player.Pause();
        _player.Source = null;
    }

    private void Post(Action action) => _dispatcher.BeginInvoke(action);

    public void Dispose() => _player.Dispose();
}
