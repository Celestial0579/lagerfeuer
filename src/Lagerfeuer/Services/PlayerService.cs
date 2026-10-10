using System.Diagnostics;
using Lagerfeuer.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using AudioTrack = Lagerfeuer.Models.AudioTrack;

namespace Lagerfeuer.Services;

/// <summary>
/// Steuert die Wiedergabe: ABS-Playback-Session, mehrere Audiotracks pro Buch,
/// Kapitel, Fortschritts-Sync, Sleep-Timer und Windows-Mediensteuerung (SMTC/Medientasten).
/// Alle öffentlichen Member werden auf dem UI-Thread verwendet.
/// </summary>
public sealed partial class PlayerService : ObservableObject
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(15);

    private readonly AbsApiClient _api;
    private readonly CacheManager _cache;
    private readonly CoverCache _covers;
    private readonly AppSettings _settings;
    private readonly DispatcherQueue _dq;
    private readonly MediaPlayer _player;
    private readonly DispatcherQueueTimer _tick;
    private readonly Stopwatch _listenWatch = new();
    private readonly Stopwatch _sinceSync = new();

    private PlaybackSession? _session;
    private int _trackIndex = -1;
    private CachingHttpStream? _stream;
    private (int Index, CachingHttpStream Stream)? _prefetch;
    private double? _pendingSeek;
    private bool _playWhenOpened;
    private bool _syncInFlight;
    private bool _wasPlaying;
    private int _loadGeneration;
    private DateTime? _sleepAt;
    private bool _sleepAtChapterEnd;

    public PlayerService(AbsApiClient api, CacheManager cache, CoverCache covers, AppSettings settings,
        DispatcherQueue dq)
    {
        _api = api;
        _cache = cache;
        _covers = covers;
        _settings = settings;
        _dq = dq;

        _player = new MediaPlayer
        {
            AutoPlay = false,
            AudioCategory = MediaPlayerAudioCategory.Media,
            Volume = Math.Clamp(settings.Volume, 0, 1),
        };
        _player.MediaOpened += (_, _) => _dq.TryEnqueue(OnMediaOpened);
        _player.MediaEnded += (_, _) => _dq.TryEnqueue(() => _ = OnMediaEndedAsync());
        _player.MediaFailed += (_, e) => _dq.TryEnqueue(() => OnMediaFailed(e));

        // Medientasten / SMTC: Weiter/Zurück springen kapitelweise.
        var cmd = _player.CommandManager;
        cmd.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cmd.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cmd.NextReceived += (_, e) => { e.Handled = true; _dq.TryEnqueue(NextChapter); };
        cmd.PreviousReceived += (_, e) => { e.Handled = true; _dq.TryEnqueue(PreviousChapter); };

        PlaybackRate = settings.PlaybackRate;
        Volume = _player.Volume;

        _tick = dq.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(250);
        _tick.Tick += (_, _) => OnTick();
    }

    // ---------- Zustand für die UI ----------

    [ObservableProperty] public partial bool HasSession { get; set; }
    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] public partial bool IsBuffering { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? ItemId { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Author { get; set; } = "";
    [ObservableProperty] public partial string ChapterTitle { get; set; } = "";
    [ObservableProperty] public partial double Position { get; set; }
    [ObservableProperty] public partial double Duration { get; set; }
    [ObservableProperty] public partial double ChapterStart { get; set; }
    [ObservableProperty] public partial double ChapterEnd { get; set; }
    [ObservableProperty] public partial double PlaybackRate { get; set; } = 1.0;
    [ObservableProperty] public partial double Volume { get; set; } = 1.0;
    [ObservableProperty] public partial double CachedFraction { get; set; }
    [ObservableProperty] public partial string? SleepTimerText { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    public IReadOnlyList<Chapter> Chapters => _session?.Chapters ?? [];

    /// <summary>Nach jedem erfolgreichen Sync/Close – z.B. um Fortschrittsanzeigen zu aktualisieren.</summary>
    public event EventHandler<(string ItemId, double CurrentTime, double Duration)>? ProgressSynced;

    partial void OnPlaybackRateChanged(double value)
    {
        _player.PlaybackSession.PlaybackRate = value;
        _settings.PlaybackRate = value;
        _settings.Save();
    }

    partial void OnVolumeChanged(double value)
    {
        _player.Volume = Math.Clamp(value, 0, 1);
        _settings.Volume = _player.Volume;
    }

    // ---------- Steuerung ----------

    public async Task PlayItemAsync(string itemId, double? startAt = null)
    {
        if (_session?.LibraryItemId == itemId)
        {
            if (startAt is { } t) await SeekAsync(t);
            Play();
            return;
        }

        await StopAsync();
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var session = await _api.StartPlaybackAsync(itemId, _settings.DeviceId);
            if (session.AudioTracks.Count == 0)
                throw new AbsApiException("Dieses Element enthält keine abspielbaren Audiodateien.");

            session.AudioTracks.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
            _session = session;
            _cache.SaveSessionMetadata(session);
            _cache.EnforceLimit((long)_settings.CacheLimitGb * 1024 * 1024 * 1024, [itemId]);

            ItemId = itemId;
            Title = session.DisplayTitle ?? "";
            Author = session.DisplayAuthor ?? "";
            Duration = session.Duration > 0 ? session.Duration : session.AudioTracks.Sum(t => t.Duration);
            OnPropertyChanged(nameof(Chapters));
            HasSession = true;

            var start = startAt ?? session.CurrentTime;
            if (start >= Duration - 2) start = 0; // fertig gehörte Bücher von vorn
            Position = start;
            UpdateChapter(start);

            _listenWatch.Reset();
            _sinceSync.Restart();
            _tick.Start();
            await LoadTrackAtAsync(start, play: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Wiedergabe von {itemId} fehlgeschlagen", ex);
            ErrorMessage = ex is AbsApiException { Status: System.Net.HttpStatusCode.NotFound }
                ? "Dieses Buch kann nicht abgespielt werden – der Server hat keine Audiodateien dafür (z. B. nur E-Book)."
                : ex.Message;
            await StopAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Play()
    {
        if (_session is null) return;
        _player.Play();
    }

    public void Pause() => _player.Pause();

    public void TogglePlayPause()
    {
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) Pause();
        else Play();
    }

    public Task SkipAsync(double seconds) => SeekAsync(CurrentBookTime() + seconds);

    public async Task SeekAsync(double bookTime)
    {
        if (_session is null) return;
        bookTime = Math.Clamp(bookTime, 0, Math.Max(0, Duration - 0.5));
        var index = FindTrackIndex(bookTime);
        Position = bookTime;
        UpdateChapter(bookTime);

        if (index == _trackIndex && _pendingSeek is null)
        {
            var track = _session.AudioTracks[index];
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(bookTime - track.StartOffset);
        }
        else
        {
            var wasPlaying = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing || _playWhenOpened;
            await LoadTrackAtAsync(bookTime, wasPlaying);
        }
    }

    public void NextChapter()
    {
        if (_session is null) return;
        var t = CurrentBookTime();
        var next = _session.Chapters.FirstOrDefault(c => c.Start > t + 0.5);
        _ = next is null ? SkipAsync(30) : SeekAsync(next.Start);
    }

    public void PreviousChapter()
    {
        if (_session is null) return;
        var t = CurrentBookTime();
        var current = _session.Chapters.LastOrDefault(c => c.Start <= t);
        if (current is null) { _ = SeekAsync(0); return; }
        if (t - current.Start > 3) { _ = SeekAsync(current.Start); return; }
        var prev = _session.Chapters.LastOrDefault(c => c.Start < current.Start);
        _ = SeekAsync(prev?.Start ?? 0);
    }

    public void SetSleepTimer(TimeSpan? duration)
    {
        _sleepAtChapterEnd = false;
        _sleepAt = duration is { } d ? DateTime.UtcNow + d : null;
        UpdateSleepText();
    }

    public void SetSleepAtChapterEnd()
    {
        _sleepAt = null;
        _sleepAtChapterEnd = true;
        UpdateSleepText();
    }

    /// <summary>Beendet die aktuelle Session und meldet den finalen Fortschritt an den Server.</summary>
    public async Task StopAsync()
    {
        _loadGeneration++;
        _tick.Stop();
        _player.Pause();
        _listenWatch.Stop();

        var session = _session;
        if (session is not null)
        {
            var current = CurrentBookTime();
            var listened = TakeListenedSeconds();
            try
            {
                await _api.CloseSessionAsync(session.Id, current, listened, Duration);
                ProgressSynced?.Invoke(this, (session.LibraryItemId, current, Duration));
            }
            catch (Exception ex)
            {
                Log.Error("Session konnte nicht geschlossen werden", ex);
                try { await _api.UpdateProgressAsync(session.LibraryItemId, current, Duration, false); }
                catch (Exception ex2) { Log.Error("Fortschritt konnte nicht gespeichert werden", ex2); }
            }
        }

        _player.Source = null;
        DisposeStreams();
        _session = null;
        _trackIndex = -1;
        _pendingSeek = null;
        _playWhenOpened = false;
        _sleepAt = null;
        _sleepAtChapterEnd = false;
        SleepTimerText = null;
        HasSession = false;
        IsPlaying = false;
        IsBuffering = false;
        ItemId = null;
        Title = Author = ChapterTitle = "";
        Position = Duration = 0;
        CachedFraction = 0;
        OnPropertyChanged(nameof(Chapters));
    }

    /// <summary>Beim Beenden der App: Session sauber schließen (mit Zeitlimit).</summary>
    public async Task ShutdownAsync()
    {
        _settings.Save();
        await Task.WhenAny(StopAsync(), Task.Delay(TimeSpan.FromSeconds(4)));
        _player.Dispose();
    }

    // ---------- Tracks laden ----------

    private int FindTrackIndex(double bookTime)
    {
        var tracks = _session!.AudioTracks;
        for (var i = tracks.Count - 1; i >= 0; i--)
            if (bookTime >= tracks[i].StartOffset) return i;
        return 0;
    }

    private double CurrentBookTime()
    {
        if (_session is null || _trackIndex < 0) return Position;
        if (_pendingSeek is { } pending) return pending;
        return _session.AudioTracks[_trackIndex].StartOffset + _player.PlaybackSession.Position.TotalSeconds;
    }

    private async Task LoadTrackAtAsync(double bookTime, bool play)
    {
        var session = _session!;
        var generation = ++_loadGeneration;
        var index = FindTrackIndex(bookTime);
        var track = session.AudioTracks[index];

        _pendingSeek = bookTime;
        _playWhenOpened = play;
        IsBuffering = true;

        try
        {
            var item = await CreatePlaybackItemAsync(session, index, track);
            if (generation != _loadGeneration) return; // inzwischen überholt

            _trackIndex = index;
            _player.Source = item;
            _ = PrefetchNextTrackAsync(index, generation);
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            Log.Error($"Track {index} konnte nicht geladen werden", ex);
            ErrorMessage = $"Audiodatei konnte nicht geladen werden: {ex.Message}";
            IsBuffering = false;
        }
    }

    private async Task<MediaPlaybackItem> CreatePlaybackItemAsync(PlaybackSession session, int index, AudioTrack track)
    {
        var itemId = session.LibraryItemId;
        MediaSource source;

        _stream?.Dispose();
        _stream = null;

        if (_cache.IsTrackComplete(itemId, track))
        {
            // Vollständig im Cache → direkt von der Platte.
            var file = await StorageFile.GetFileFromPathAsync(_cache.GetTrackPath(itemId, track));
            source = MediaSource.CreateFromStorageFile(file);
            CachedFraction = 1;
        }
        else
        {
            CachingHttpStream stream;
            if (_prefetch is { } p && p.Index == index)
            {
                stream = p.Stream;
                _prefetch = null;
            }
            else
            {
                stream = await _cache.OpenTrackAsync(itemId, track);
            }
            _stream = stream;
            stream.Completed += (_, _) => _dq.TryEnqueue(() => _ = PrefetchNextTrackAsync(index, _loadGeneration));
            source = MediaSource.CreateFromStream(stream.AsRandomAccessStream(), track.MimeType ?? "audio/mpeg");
        }

        var item = new MediaPlaybackItem(source);
        await ApplyDisplayPropertiesAsync(item, session);
        return item;
    }

    /// <summary>Lädt den nächsten Track im Hintergrund vor, sobald der aktuelle vollständig im Cache ist.</summary>
    private async Task PrefetchNextTrackAsync(int currentIndex, int generation)
    {
        try
        {
            var session = _session;
            if (session is null || generation != _loadGeneration) return;
            if (_stream is { IsComplete: false }) return;

            var nextIndex = currentIndex + 1;
            if (nextIndex >= session.AudioTracks.Count || _prefetch?.Index == nextIndex) return;
            var next = session.AudioTracks[nextIndex];
            if (_cache.IsTrackComplete(session.LibraryItemId, next)) return;

            var stream = await _cache.OpenTrackAsync(session.LibraryItemId, next);
            if (generation != _loadGeneration || _session != session)
            {
                stream.Dispose();
                return;
            }
            _prefetch?.Stream.Dispose();
            _prefetch = (nextIndex, stream);
        }
        catch (Exception ex)
        {
            Log.Error("Vorladen des nächsten Tracks fehlgeschlagen", ex);
        }
    }

    private async Task ApplyDisplayPropertiesAsync(MediaPlaybackItem item, PlaybackSession session)
    {
        var props = item.GetDisplayProperties();
        props.Type = MediaPlaybackType.Music;
        props.MusicProperties.Title = session.DisplayTitle ?? "";
        props.MusicProperties.Artist = session.DisplayAuthor ?? "";
        props.MusicProperties.AlbumTitle = session.DisplayTitle ?? "";
        try
        {
            var cover = await _covers.GetCoverPathAsync(session.LibraryItemId);
            if (cover is not null)
                props.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(cover));
        }
        catch (Exception ex)
        {
            Log.Error("Cover für Mediensteuerung nicht verfügbar", ex);
        }
        item.ApplyDisplayProperties(props);
    }

    private void DisposeStreams()
    {
        _stream?.Dispose();
        _stream = null;
        _prefetch?.Stream.Dispose();
        _prefetch = null;
    }

    // ---------- MediaPlayer-Events (UI-Thread) ----------

    private void OnMediaOpened()
    {
        if (_session is null || _trackIndex < 0) return;
        _player.PlaybackSession.PlaybackRate = PlaybackRate;
        if (_pendingSeek is { } t)
        {
            var offset = t - _session.AudioTracks[_trackIndex].StartOffset;
            if (offset > 0.25) _player.PlaybackSession.Position = TimeSpan.FromSeconds(offset);
            _pendingSeek = null;
        }
        IsBuffering = false;
        if (_playWhenOpened) _player.Play();
        _playWhenOpened = false;
    }

    private async Task OnMediaEndedAsync()
    {
        if (_session is null) return;
        var next = _trackIndex + 1;
        if (next < _session.AudioTracks.Count)
        {
            await LoadTrackAtAsync(_session.AudioTracks[next].StartOffset, play: true);
            return;
        }

        // Buch zu Ende
        Position = Duration;
        IsPlaying = false;
        await SyncAsync(force: true);
        try
        {
            await _api.UpdateProgressAsync(_session.LibraryItemId, Duration, Duration, isFinished: true);
            ProgressSynced?.Invoke(this, (_session.LibraryItemId, Duration, Duration));
        }
        catch (Exception ex)
        {
            Log.Error("Konnte Buch nicht als beendet markieren", ex);
        }
    }

    private void OnMediaFailed(MediaPlayerFailedEventArgs e)
    {
        Log.Error($"MediaPlayer-Fehler: {e.Error} {e.ErrorMessage}", e.ExtendedErrorCode);
        ErrorMessage = $"Wiedergabefehler: {e.ErrorMessage}";
        IsBuffering = false;
        IsPlaying = false;
    }

    private void OnTick()
    {
        if (_session is null) return;

        var state = _player.PlaybackSession.PlaybackState;
        var playing = state == MediaPlaybackState.Playing;
        IsPlaying = playing || _playWhenOpened;
        if (state == MediaPlaybackState.Buffering || state == MediaPlaybackState.Opening) IsBuffering = true;
        else if (_pendingSeek is null) IsBuffering = false;

        if (playing && !_listenWatch.IsRunning) _listenWatch.Start();
        if (!playing && _listenWatch.IsRunning) _listenWatch.Stop();

        var t = CurrentBookTime();
        Position = t;
        UpdateChapter(t);
        CachedFraction = _stream?.CachedFraction ?? (_trackIndex >= 0 ? 1 : 0);

        CheckSleepTimer(t);

        // Pause → sofort syncen; sonst periodisch.
        if (_wasPlaying && !playing) _ = SyncAsync(force: true);
        else if (playing && _sinceSync.Elapsed >= SyncInterval) _ = SyncAsync(force: false);
        _wasPlaying = playing;
    }

    private void UpdateChapter(double t)
    {
        var chapters = _session?.Chapters;
        var ch = chapters?.LastOrDefault(c => c.Start <= t);
        if (ch is null)
        {
            ChapterTitle = "";
            ChapterStart = 0;
            ChapterEnd = Duration;
            return;
        }
        ChapterTitle = ch.Title;
        ChapterStart = ch.Start;
        ChapterEnd = ch.End;
    }

    // ---------- Sleep-Timer ----------

    private void CheckSleepTimer(double t)
    {
        if (_sleepAt is { } at)
        {
            if (DateTime.UtcNow >= at)
            {
                Pause();
                SetSleepTimer(null);
            }
            else UpdateSleepText();
        }
        else if (_sleepAtChapterEnd && ChapterEnd > 0 && t >= ChapterEnd - 0.4)
        {
            Pause();
            SetSleepTimer(null);
        }
    }

    private void UpdateSleepText()
    {
        if (_sleepAt is { } at)
        {
            var left = at - DateTime.UtcNow;
            SleepTimerText = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"m\:ss");
        }
        else SleepTimerText = _sleepAtChapterEnd ? "Kapitelende" : null;
    }

    // ---------- Fortschritts-Sync ----------

    private double TakeListenedSeconds()
    {
        var seconds = _listenWatch.Elapsed.TotalSeconds + _carryOverListened;
        _carryOverListened = 0;
        _listenWatch.Restart();
        if (!IsPlaying) _listenWatch.Stop();
        return Math.Round(seconds, 1);
    }

    private async Task SyncAsync(bool force)
    {
        var session = _session;
        if (session is null || _syncInFlight) return;
        if (!force && _sinceSync.Elapsed < SyncInterval) return;

        _syncInFlight = true;
        _sinceSync.Restart();
        var listened = TakeListenedSeconds();
        var current = CurrentBookTime();
        try
        {
            await _api.SyncSessionAsync(session.Id, current, listened, Duration);
            ProgressSynced?.Invoke(this, (session.LibraryItemId, current, Duration));
        }
        catch (AbsApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            // Session ist am Server nicht mehr offen (Neustart oder > 36 h inaktiv) → neue Session,
            // Wiedergabe läuft lokal ungestört weiter. Tracks/URLs bleiben identisch.
            Log.Info($"Session {session.Id} am Server geschlossen – starte neue Session.");
            try
            {
                var fresh = await _api.StartPlaybackAsync(session.LibraryItemId, _settings.DeviceId);
                if (_session == session)
                {
                    session.Id = fresh.Id;
                    await _api.SyncSessionAsync(session.Id, current, listened, Duration);
                }
            }
            catch (Exception ex2)
            {
                Log.Error("Neue Session konnte nicht gestartet werden", ex2);
                _carryOverListened += listened;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Fortschritts-Sync fehlgeschlagen", ex);
            // Gehörte Zeit nicht verlieren: beim nächsten Sync mitschicken.
            _carryOverListened += listened;
        }
        finally
        {
            _syncInFlight = false;
        }
    }

    private double _carryOverListened;
}
