using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Notch.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Notch.Services;

/// <summary>
/// Spotify über die Windows-Mediensteuerung (GlobalSystemMediaTransportControls).
/// Sichtbar nur während der Wiedergabe; bei Pause nach 5 s ausblenden.
/// </summary>
public sealed class MediaService : IDisposable
{
    private const string Source = "spotify";
    private static readonly TimeSpan HideAfterPause = TimeSpan.FromSeconds(5);

    private readonly EventResolver _resolver;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _pauseHide;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _trackKey = "";
    private ImageSource? _artwork;
    private bool _shown;

    public MediaService(EventResolver resolver, Dispatcher ui)
    {
        _resolver = resolver;
        _ui = ui;
        _pauseHide = new DispatcherTimer { Interval = HideAfterPause };
        _pauseHide.Tick += (_, _) => { _pauseHide.Stop(); HideMedia(); };
    }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => _ui.InvokeAsync(AttachSpotify);
            AttachSpotify();
            Log.Info("Mediensteuerung aktiv");
        }
        catch (Exception ex)
        {
            Log.Error("Mediensteuerung nicht verfügbar", ex);
        }
    }

    public async void Command(string command)
    {
        var s = _session;
        if (s is null) return;
        try
        {
            _ = command switch
            {
                "prev" => await s.TrySkipPreviousAsync(),
                "toggle" => await s.TryTogglePlayPauseAsync(),
                "next" => await s.TrySkipNextAsync(),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            Log.Error($"Medienbefehl '{command}' fehlgeschlagen", ex);
        }
    }

    private void AttachSpotify()
    {
        var spotify = _manager?.GetSessions()
            .FirstOrDefault(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));

        if (ReferenceEquals(spotify, _session)) return;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
        }

        _session = spotify;
        _trackKey = "";
        _artwork = null;

        if (_session is null)
        {
            HideMedia();
            return;
        }

        _session.MediaPropertiesChanged += OnSessionChanged;
        _session.PlaybackInfoChanged += OnSessionChanged;
        _ = RefreshAsync();
    }

    // WinRT-Events kommen auf Hintergrund-Threads
    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _ui.InvokeAsync(() => _ = RefreshAsync());

    private async Task RefreshAsync()
    {
        var s = _session;
        if (s is null) return;

        GlobalSystemMediaTransportControlsSessionMediaProperties? props;
        try
        {
            props = await s.TryGetMediaPropertiesAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Medien-Infos nicht lesbar", ex);
            return;
        }
        if (!ReferenceEquals(s, _session)) return;

        if (props is null || string.IsNullOrWhiteSpace(props.Title))
        {
            HideMedia();
            return;
        }

        var playing = s.GetPlaybackInfo()?.PlaybackStatus ==
                      GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var key = $"{props.Title}\u001f{props.Artist}";
        if (key != _trackKey || _artwork is null)
        {
            _trackKey = key;
            _artwork = await LoadArtworkAsync(props.Thumbnail);
        }

        if (playing)
        {
            _pauseHide.Stop();
            Push(props.Title, props.Artist ?? "", playing: true);
        }
        else if (_shown)
        {
            Push(props.Title, props.Artist ?? "", playing: false);
            if (!_pauseHide.IsEnabled) _pauseHide.Start();
        }
    }

    private void Push(string title, string artist, bool playing)
    {
        _resolver.Submit(new NotchEvent(Source, EventKind.Media, title, artist,
            Sticky: true, Media: new MediaInfo(artist, playing, _artwork)));
        _shown = true;
    }

    private void HideMedia()
    {
        _pauseHide.Stop();
        if (!_shown) return;
        _resolver.Dismiss(Source, null);
        _shown = false;
    }

    private static async Task<ImageSource?> LoadArtworkAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        try
        {
            using var winStream = await thumbnail.OpenReadAsync();
            using var stream = winStream.AsStreamForRead();
            var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 96;
            bmp.StreamSource = buffer;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Error("Cover nicht ladbar", ex);
            return null;
        }
    }

    public void Dispose()
    {
        _pauseHide.Stop();
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
        }
    }
}
