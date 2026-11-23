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
    private readonly LocalSessionStore _localSessions;
    private readonly ProgressStore _progress;
    private readonly DispatcherQueue _dq;
    private readonly MediaPlayer _player;
    private readonly DispatcherQueueTimer _tick;
    private readonly Stopwatch _listenWatch = new();
    private readonly Stopwatch _sinceSync = new();

    private PlaybackSession? _session;
    private LocalSession? _localSession; // gesetzt bei Wiedergabe ohne Server
    private int _trackIndex = -1;
    private CachingHttpStream? _stream;
    private (int Index, CachingHttpStream Stream)? _prefetch;
    private bool _prefetching;
    private bool _prefetchTriedForPosition;
    private double? _pendingSeek;
    private bool _playWhenOpened;
    private bool _syncInFlight;
    private bool _wasPlaying;
    private int _loadGeneration;
    private DateTime? _sleepAt;
    private bool _sleepAtChapterEnd;
    // Abgleich je Session getrennt halten (#36, vgl. audiobookshelf-app#1491); null bei Offline-Wiedergabe.
    private readonly SessionSyncGuard _guard = new();
    private SessionTicket? _ticket;
    private PlaylistQueue? _queue;

    public PlayerService(AbsApiClient api, CacheManager cache, CoverCache covers, AppSettings settings,
        LocalSessionStore localSessions, ProgressStore progress, DispatcherQueue dq)
    {
        _api = api;
        _cache = cache;
        _covers = covers;
        _settings = settings;
        _localSessions = localSessions;
        _progress = progress;
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
    /// <summary>Läuft die aktuelle Wiedergabe ohne Server (heruntergeladenes Buch)?</summary>
    [ObservableProperty] public partial bool IsOfflineSession { get; set; }
    /// <summary>Läuft eine Wiedergabeliste? Dann z.B. „Liste · 2 von 5“, sonst null.</summary>
    [ObservableProperty] public partial string? QueueText { get; set; }
    [ObservableProperty] public partial bool HasNextInQueue { get; set; }

    /// <summary>App ist offline: heruntergeladene Bücher direkt lokal abspielen, ohne Server-Versuch.</summary>
    public bool PreferOffline { get; set; }

    /// <summary>Eine Offline-Session wurde beendet – Gelegenheit zum Hochladen.</summary>
    public event EventHandler? LocalSessionEnded;

    public IReadOnlyList<Chapter> Chapters => _session?.Chapters ?? [];

    /// <summary>Id der laufenden Server-Session (um eigene Live-Sync-Meldungen zu erkennen).</summary>
    public string? SessionId => _session?.Id;

    /// <summary>Stammt eine Live-Meldung von einer Session dieses Geräts, auch einer schon geschlossenen?</summary>
    public bool IsOwnSession(string? sessionId) => _guard.IsOwnSession(sessionId);

    /// <summary>Folgen-Id bei Podcasts (sonst null).</summary>
    public string? EpisodeId => _session?.EpisodeId;

    /// <summary>Nach jedem erfolgreichen Sync/Close – z.B. um Fortschrittsanzeigen zu aktualisieren.</summary>
    public event EventHandler<(string ItemId, string? EpisodeId, double CurrentTime, double Duration)>? ProgressSynced;

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

    /// <summary>Spielt ein Buch bzw. (mit episodeId) eine Podcast-Folge. Eine laufende Wiedergabeliste endet damit.</summary>
    public Task PlayItemAsync(string itemId, double? startAt = null, string? episodeId = null)
    {
        if (_queue is not null && !_queue.IsCurrent(itemId, episodeId)) SetQueue(null);
        return PlayItemCoreAsync(itemId, startAt, episodeId);
    }

    /// <summary>
    /// Spielt eine Wiedergabeliste ab dem Eintrag <paramref name="startIndex"/>. Nach dem Ende eines Titels
    /// startet der nächste. Ausgelöst wird das nur von Hand, nie durch eine Meldung des Servers.
    /// </summary>
    public async Task PlayPlaylistAsync(Playlist playlist, int startIndex = 0)
    {
        var queue = new PlaylistQueue(playlist, startIndex);
        if (queue.Current is not { } first)
        {
            ErrorMessage = "In dieser Wiedergabeliste gibt es nichts, das sich abspielen lässt.";
            return;
        }
        SetQueue(queue);
        await PlayItemCoreAsync(first.LibraryItemId, null, first.EpisodeId);
    }

    /// <summary>Springt in der laufenden Wiedergabeliste zum nächsten Titel.</summary>
    public async Task PlayNextInQueueAsync()
    {
        if (_queue?.MoveNext() is not { } next)
        {
            SetQueue(null);
            return;
        }
        UpdateQueueState();
        await PlayItemCoreAsync(next.LibraryItemId, null, next.EpisodeId);
    }

    private void SetQueue(PlaylistQueue? queue)
    {
        _queue = queue;
        UpdateQueueState();
    }

    private void UpdateQueueState()
    {
        QueueText = _queue?.Describe();
        HasNextInQueue = _queue?.HasNext == true;
    }

    private async Task PlayItemCoreAsync(string itemId, double? startAt, string? episodeId)
    {
        if (_session is not null && _session.LibraryItemId == itemId && _session.EpisodeId == episodeId)
        {
            if (startAt is { } t) await SeekAsync(t);
            Play();
            return;
        }

        await StopCoreAsync();
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var session = await StartSessionAsync(itemId, episodeId);
            if (session.AudioTracks.Count == 0)
                throw new AbsApiException("Dieses Element enthält keine abspielbaren Audiodateien.");

            session.AudioTracks.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
            _session = session;
            if (_localSession is null && episodeId is null)
            {
                // Buchdaten lokal sichern: ist alles gehört/geladen, geht es später auch offline.
                if (_cache.GetLocalItem(itemId) is null) _cache.SaveLocalItem(LocalItem.FromSession(session));
                _cache.EnforceLimit((long)_settings.CacheLimitGb * 1024 * 1024 * 1024, [itemId]);
            }

            ItemId = itemId;
            Title = session.DisplayTitle ?? "";
            Author = session.DisplayAuthor ?? "";
            Duration = session.Duration > 0 ? session.Duration : session.AudioTracks.Sum(t => t.Duration);
            // Die vorige Session ist hier schon geschlossen (StopAsync oben). Erst jetzt beginnt die neue.
            _ticket = _localSession is null ? _guard.Begin(session.Id, itemId, episodeId, Duration) : null;
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
            ErrorMessage = ex switch
            {
                // Antwort des Servers selbst: Das Buch gibt es dort nicht oder es hat keine Audiodateien.
                AbsApiException { Status: System.Net.HttpStatusCode.NotFound, ServerUnreachable: false }
                    => "Dieses Buch kann nicht abgespielt werden – der Server hat keine Audiodateien dafür (z. B. nur E-Book).",
                // Ein Proxy davor meldet, dass der Server fehlt. Ein heruntergeladenes Buch hätte hier schon gespielt.
                AbsApiException { ServerUnreachable: true, Status: { } status }
                    => $"Der Audiobookshelf-Server ist gerade nicht erreichbar (ein Proxy davor antwortet mit {(int)status}). " +
                       "Heruntergeladene Bücher kannst du weiter hören.",
                _ => ex.Message,
            };
            await StopCoreAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Startet eine Session am Server – oder, wenn dieser nicht erreichbar ist und das Buch vollständig
    /// lokal vorliegt, eine Offline-Session, die später hochgeladen wird. Nicht erreichbar heißt: keine
    /// Verbindung, oder ein Proxy davor meldet, dass der Server fehlt (<see cref="AbsApiException.ServerUnreachable"/>).
    /// </summary>
    private async Task<PlaybackSession> StartSessionAsync(string itemId, string? episodeId)
    {
        _localSession = null;
        IsOfflineSession = false;
        if (!PreferOffline)
        {
            try
            {
                return await _api.StartPlaybackAsync(itemId, _settings.DeviceId, episodeId);
            }
            catch (AbsApiException ex) when (ex.ServerUnreachable && CanPlayOffline(itemId, episodeId))
            {
                Log.Info($"Server nicht erreichbar ({ex.Message}) – spiele {ProgressStore.Key(itemId, episodeId)} offline.");
            }
        }

        // Bücher aus item.json, heruntergeladene Podcast-Folgen aus ihren eigenen Daten im Podcast-Ordner (#76).
        var local = episodeId is null ? _cache.GetLocalItem(itemId) : _cache.GetLocalEpisode(itemId, episodeId);
        if (local is null || !_cache.IsAvailableOffline(local))
            throw new AbsApiException(episodeId is null
                ? "Offline nicht verfügbar: Dieses Buch ist nicht vollständig heruntergeladen."
                : "Offline nicht verfügbar: Diese Folge ist nicht heruntergeladen.");

        var progress = _progress.Get(itemId, episodeId);
        var start = progress is { IsFinished: false } ? progress.CurrentTime : 0;
        var session = local.ToOfflineSession("local-" + Guid.NewGuid().ToString("N"), start);
        _localSession = new LocalSession
        {
            LibraryItemId = itemId,
            EpisodeId = episodeId,
            MediaType = episodeId is null ? "book" : "podcast",
            DisplayTitle = local.Title,
            DisplayAuthor = local.Author,
            Duration = local.Duration,
            StartTime = start,
            CurrentTime = start,
            DeviceInfo = AbsApiClient.CreateDeviceInfo(_settings.DeviceId),
        };
        _localSessions.Upsert(_localSession);
        IsOfflineSession = true;
        return session;
    }

    private bool CanPlayOffline(string itemId, string? episodeId = null)
        => (episodeId is null ? _cache.GetLocalItem(itemId) : _cache.GetLocalEpisode(itemId, episodeId)) is { } local &&
           _cache.IsAvailableOffline(local);

    public void Play()
    {
        _calls?.OnUserAction();
        PlayCore();
    }

    public void Pause()
    {
        _calls?.OnUserAction();
        _player.Pause();
    }

    public void TogglePlayPause()
    {
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) Pause();
        else Play();
    }

    private void PlayCore()
    {
        if (_session is null) return;
        _player.Play();
    }

    // ---------- Anrufe ----------

    private CallPauseController? _calls;

    /// <summary>Verbindet die Anruferkennung: bei Anrufen pausieren, danach optional fortsetzen.</summary>
    public void AttachCallDetector(CallDetector detector)
    {
        _calls = new CallPauseController(
            isPlaying: () => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing,
            pause: () => _player.Pause(),
            play: PlayCore,
            autoPause: () => _settings.PauseOnCalls,
            autoResume: () => _settings.ResumeAfterCalls);
        detector.CallStarted += (_, _) => _dq.TryEnqueue(() =>
        {
            _calls.OnCallStarted();
            if (_calls.PausedByCall) Log.Info("Anruf erkannt – Wiedergabe pausiert.");
        });
        detector.CallEnded += (_, _) => _dq.TryEnqueue(() => _calls.OnCallEnded());
    }

    public Task SkipAsync(double seconds) => SeekAsync(CurrentBookTime() + seconds);

    public async Task SeekAsync(double bookTime)
    {
        if (_session is null) return;
        bookTime = Math.Clamp(bookTime, 0, Math.Max(0, Duration - 0.5));
        var index = FindTrackIndex(bookTime);
        Position = bookTime;
        UpdateChapter(bookTime);
        // Nach einem Sprung neu prüfen, ob der Rest der Datei schon da ist (siehe OnTick).
        _prefetchTriedForPosition = false;

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

    /// <summary>Beendet die aktuelle Session (und eine laufende Wiedergabeliste) und meldet den finalen Fortschritt.</summary>
    public Task StopAsync()
    {
        SetQueue(null);
        return StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        _loadGeneration++;
        _tick.Stop();
        _player.Pause();
        _listenWatch.Stop();

        var session = _session;
        if (session is not null && _localSession is { } local)
        {
            RecordLocal(local, CurrentBookTime(), TakeListenedSeconds());
            _localSession = null;
            LocalSessionEnded?.Invoke(this, EventArgs.Empty);
        }
        else if (session is not null && _ticket is { } ticket)
        {
            // Wartet auf einen laufenden Abgleich; danach geht nur noch der letzte Stand an den Server.
            await _guard.EndAsync(ticket, async t =>
            {
                var current = CurrentBookTime();
                var listened = TakeListenedSeconds(t);
                try
                {
                    await _api.CloseSessionAsync(t.SessionId, current, listened, t.Duration);
                    ProgressSynced?.Invoke(this, (t.ItemId, t.EpisodeId, current, t.Duration));
                }
                catch (Exception ex)
                {
                    Log.Error("Session konnte nicht geschlossen werden", ex);
                    try { await _api.UpdateProgressAsync(t.ItemId, current, t.Duration, false, t.EpisodeId); }
                    catch (Exception ex2) { Log.Error("Fortschritt konnte nicht gespeichert werden", ex2); }
                }
            });
        }
        _ticket = null;

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
        IsOfflineSession = false;
        IsPlaying = false;
        IsBuffering = false;
        ItemId = null;
        Title = Author = ChapterTitle = "";
        Position = Duration = 0;
        CachedFraction = 0;
        OnPropertyChanged(nameof(Chapters));
    }

    private RunOnce? _shutdown;

    /// <summary>
    /// Beim Beenden der App: Session sauber schließen (mit Zeitlimit). Läuft je Instanz nur einmal, gleich
    /// wie oft und von wo es angestoßen wird (Fenster schließen, Update); weitere Aufrufe warten auf denselben
    /// Vorgang.
    /// </summary>
    public Task ShutdownAsync() => (_shutdown ??= new RunOnce(ShutdownCoreAsync)).RunAsync();

    private async Task ShutdownCoreAsync()
    {
        _settings.Save();
        await Task.WhenAny(StopCoreAsync(), Task.Delay(TimeSpan.FromSeconds(4)));
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
        _prefetchTriedForPosition = false;

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

    /// <summary>
    /// Lädt den nächsten Track im Hintergrund vor, sobald der aktuelle den Server nicht mehr braucht: wenn er
    /// von der Lesestelle bis zum Dateiende im Cache ist. Vollständig muss er dafür nicht sein. Nach einem
    /// Sprung fehlt der Teil davor; würde darauf gewartet, käme das Vorladen zu spät oder gar nicht, und am
    /// Dateiende stockte die Wiedergabe, bis der Anfang der nächsten Datei geladen ist.
    /// </summary>
    private async Task PrefetchNextTrackAsync(int currentIndex, int generation)
    {
        if (_prefetching) return;
        var started = false;
        try
        {
            var session = _session;
            if (session is null || generation != _loadGeneration) return;
            if (_stream is { } current && !current.IsCompleteFrom(current.Position)) return;

            var nextIndex = currentIndex + 1;
            if (nextIndex >= session.AudioTracks.Count || _prefetch?.Index == nextIndex) return;
            var next = session.AudioTracks[nextIndex];
            if (_cache.IsTrackComplete(session.LibraryItemId, next)) return;

            _prefetching = started = true;
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
        finally
        {
            if (started) _prefetching = false;
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
        var session = _session;
        if (session is null) return;
        var next = _trackIndex + 1;
        if (next < session.AudioTracks.Count)
        {
            await LoadTrackAtAsync(session.AudioTracks[next].StartOffset, play: true);
            return;
        }

        // Buch zu Ende. Alles Weitere gilt nur für genau diese Session: Wurde inzwischen etwas anderes
        // gestartet, wird weder der neue Titel als beendet markiert noch weitergesprungen.
        var ticket = _ticket;
        Position = Duration;
        IsPlaying = false;
        await SyncAsync(force: true);
        if (_session != session) return;
        if (ticket is not null) // Offline: "beendet" ergibt sich beim Hochladen aus der Position
        {
            await _guard.SyncAsync(ticket, async t =>
            {
                try
                {
                    await _api.UpdateProgressAsync(t.ItemId, t.Duration, t.Duration, isFinished: true, episodeId: t.EpisodeId);
                    ProgressSynced?.Invoke(this, (t.ItemId, t.EpisodeId, t.Duration, t.Duration));
                }
                catch (Exception ex)
                {
                    Log.Error("Konnte Buch nicht als beendet markieren", ex);
                }
            });
        }

        if (_session == session && _queue is { } queue && queue.IsCurrent(session.LibraryItemId, session.EpisodeId))
            await PlayNextInQueueAsync(); // schließt die Session dieses Titels, bevor der nächste beginnt
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

        // Vorladen des nächsten Tracks anstoßen, sobald der Rest des laufenden da ist. Einmal je Stelle:
        // Nach einem Sprung oder Trackwechsel wird neu geprüft. Erst nach dem Öffnen (_pendingSeek ist dann
        // leer): Beim Öffnen liest Windows kurz am Dateiende. Die Lesestelle steht in dem Moment dort, und ab
        // dort ist alles geladen, obwohl fast die ganze Datei noch fehlt.
        if (!_prefetchTriedForPosition && _pendingSeek is null
            && _stream is { IsComplete: false } stream && stream.IsCompleteFrom(stream.Position))
        {
            _prefetchTriedForPosition = true;
            _ = PrefetchNextTrackAsync(_trackIndex, _loadGeneration);
        }

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

    private double TakeListenedSeconds(SessionTicket? ticket = null)
    {
        var seconds = _listenWatch.Elapsed.TotalSeconds + (ticket?.CarryOverListened ?? 0);
        if (ticket is not null) ticket.CarryOverListened = 0;
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
        try
        {
            if (_localSession is { } local)
            {
                RecordLocal(local, CurrentBookTime(), TakeListenedSeconds());
                return;
            }
            if (_ticket is not { } ticket) return;
            // Stand und gehörte Zeit erst nehmen, wenn der Abgleich dran ist – und nur, solange die Session gilt.
            await _guard.SyncAsync(ticket, t => SendSyncAsync(session, t));
        }
        finally
        {
            _syncInFlight = false;
        }
    }

    private async Task SendSyncAsync(PlaybackSession session, SessionTicket t)
    {
        var listened = TakeListenedSeconds(t);
        var current = CurrentBookTime();
        try
        {
            await _api.SyncSessionAsync(t.SessionId, current, listened, t.Duration);
            ProgressSynced?.Invoke(this, (t.ItemId, t.EpisodeId, current, t.Duration));
        }
        catch (AbsApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound && !ex.ServerUnreachable)
        {
            // Session ist am Server nicht mehr offen (Neustart oder > 36 h inaktiv) → neue Session,
            // Wiedergabe läuft lokal ungestört weiter. Tracks/URLs bleiben identisch. Hier läuft noch die
            // alte Session (der Guard lässt kein Schließen dazwischen), also gehört die neue zum selben Titel.
            Log.Info($"Session {t.SessionId} am Server geschlossen – starte neue Session.");
            try
            {
                var fresh = await _api.StartPlaybackAsync(t.ItemId, _settings.DeviceId, t.EpisodeId);
                t.SessionId = session.Id = fresh.Id;
                _guard.RememberOwn(fresh.Id);
                await _api.SyncSessionAsync(t.SessionId, current, listened, t.Duration);
            }
            catch (Exception ex2)
            {
                Log.Error("Neue Session konnte nicht gestartet werden", ex2);
                t.CarryOverListened += listened;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Fortschritts-Sync fehlgeschlagen", ex);
            // Gehörte Zeit nicht verlieren: beim nächsten Sync dieser Session mitschicken.
            t.CarryOverListened += listened;
        }
    }

    /// <summary>Fortschritt einer Offline-Session lokal festhalten (wird später hochgeladen).</summary>
    private void RecordLocal(LocalSession local, double current, double listened)
    {
        local.CurrentTime = current;
        local.TimeListening = Math.Round(local.TimeListening + listened, 1);
        local.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _localSessions.Upsert(local);
        ProgressSynced?.Invoke(this, (local.LibraryItemId, local.EpisodeId, current, local.Duration));
    }
}
