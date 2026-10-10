using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace Lagerfeuer.Services;

/// <summary>
/// Seekbarer Stream über eine Audiodatei auf dem ABS-Server, der beim Abspielen
/// gleichzeitig auf die Platte cached ("Caching beim Streaming").
///
/// - Die Datei wird in Blöcke (Chunks) eingeteilt; eine .map-Datei merkt sich, welche Blöcke vorhanden sind.
/// - Ein einzelner Download-Loop lädt sequenziell per HTTP-Range ab der Position, die der Player braucht,
///   und lädt danach den Rest der Datei im Hintergrund vor.
/// - Springt der Player an eine noch nicht geladene Stelle, wird der Download dort neu gestartet.
/// - Ist die Datei komplett, kann sie direkt (und in v2 offline) von der Platte gespielt werden.
/// </summary>
public sealed class CachingHttpStream : Stream
{
    public const int ChunkSize = 256 * 1024;
    private const int MapHeaderSize = sizeof(long) + sizeof(int);
    private const int ContinueWindowChunks = 8; // liegt die Wunschposition so nah voraus, wird nicht neu gestartet
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    private readonly AbsApiClient _api;
    private readonly string _contentPath;
    private readonly string _mapPath;
    private readonly FileStream _file;
    private readonly object _fileLock = new();
    private readonly bool[] _have;
    private readonly int _chunkCount;
    private readonly CancellationTokenSource _cts = new();

    private int _haveCount;
    private long _position;
    private volatile int _wantChunk;
    private int _wantVersion;
    private TaskCompletionSource _signal = NewSignal();
    private Task? _downloader;
    private volatile Exception? _lastError;
    private bool _rangeSupported = true;
    private bool _fileClosed;
    private bool _disposed;

    public string DataPath { get; }
    public override long Length { get; }
    public bool IsComplete => Volatile.Read(ref _haveCount) == _chunkCount;
    public double CachedFraction => _chunkCount == 0 ? 1 : (double)Volatile.Read(ref _haveCount) / _chunkCount;

    /// <summary>Wird (auf einem Hintergrund-Thread) ausgelöst, sobald die Datei vollständig gecached ist.</summary>
    public event EventHandler? Completed;

    private CachingHttpStream(AbsApiClient api, string contentPath, string dataPath, long length, bool[] have)
    {
        _api = api;
        _contentPath = contentPath;
        DataPath = dataPath;
        _mapPath = MapPathFor(dataPath);
        Length = length;
        _chunkCount = have.Length;
        _have = have;
        _haveCount = have.Count(h => h);

        _file = new FileStream(dataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        if (_file.Length != length) _file.SetLength(length);
    }

    public static string MapPathFor(string dataPath) => dataPath + ".map";

    /// <summary>Prüft anhand der .map-Datei, ob eine Datei bereits vollständig gecached ist.</summary>
    public static bool IsFileComplete(string dataPath)
        => TryLoadMap(dataPath, out _, out var have) && have.All(h => h);

    /// <summary>Anteil der bereits lokal gespeicherten Daten laut .map-Datei (0 bis 1).</summary>
    public static double GetCachedFraction(string dataPath)
        => TryLoadMap(dataPath, out _, out var have) && have.Length > 0 ? (double)have.Count(h => h) / have.Length : 0;

    public static async Task<CachingHttpStream> OpenAsync(AbsApiClient api, string contentPath, string dataPath,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);

        if (!TryLoadMap(dataPath, out var length, out var have))
        {
            length = await ProbeLengthAsync(api, contentPath, ct);
            have = new bool[(int)((length + ChunkSize - 1) / ChunkSize)];
        }

        var stream = new CachingHttpStream(api, contentPath, dataPath, length, have);
        stream.SaveMap();
        if (!stream.IsComplete) stream.EnsureDownloader();
        return stream;
    }

    private static async Task<long> ProbeLengthAsync(AbsApiClient api, string contentPath, CancellationToken ct)
    {
        using var resp = await api.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, api.Resolve(contentPath));
            req.Headers.Range = new RangeHeaderValue(0, 0);
            return req;
        }, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
            throw new AbsApiException($"Audiodatei nicht abrufbar ({(int)resp.StatusCode}).", resp.StatusCode);

        var length = resp.StatusCode == HttpStatusCode.PartialContent
            ? resp.Content.Headers.ContentRange?.Length
            : resp.Content.Headers.ContentLength;
        return length ?? throw new AbsApiException("Server liefert keine Dateigröße für die Audiodatei.");
    }

    // ---------- Map-Datei ----------

    private static bool TryLoadMap(string dataPath, out long length, out bool[] have)
    {
        length = 0;
        have = [];
        try
        {
            var mapPath = MapPathFor(dataPath);
            if (!File.Exists(mapPath) || !File.Exists(dataPath)) return false;
            byte[] bytes;
            lock (MapLockFor(mapPath)) bytes = File.ReadAllBytes(mapPath);
            if (bytes.Length < MapHeaderSize) return false;
            length = BitConverter.ToInt64(bytes, 0);
            var chunkSize = BitConverter.ToInt32(bytes, sizeof(long));
            var count = (int)((length + ChunkSize - 1) / ChunkSize);
            if (chunkSize != ChunkSize || length <= 0 || bytes.Length != MapHeaderSize + count) return false;
            if (new FileInfo(dataPath).Length != length) return false;
            have = new bool[count];
            for (var i = 0; i < count; i++) have[i] = bytes[MapHeaderSize + i] != 0;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Cache-Map unlesbar: {dataPath}", ex);
            return false;
        }
    }

    // Mehrere Streams können kurzzeitig dieselbe Datei bedienen (z.B. beim Springen zwischen Tracks,
    // während der alte Stream noch abschließt). Map-Zugriffe sind daher pro Datei prozessweit gesperrt.
    private static readonly ConcurrentDictionary<string, object> MapLocks = new(StringComparer.OrdinalIgnoreCase);

    private static object MapLockFor(string mapPath) => MapLocks.GetOrAdd(mapPath, _ => new object());

    private void SaveMap()
    {
        lock (MapLockFor(_mapPath))
        {
            var tmp = $"{_mapPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                var bytes = new byte[MapHeaderSize + _chunkCount];
                BitConverter.TryWriteBytes(bytes.AsSpan(0, sizeof(long)), Length);
                BitConverter.TryWriteBytes(bytes.AsSpan(sizeof(long), sizeof(int)), ChunkSize);
                for (var i = 0; i < _chunkCount; i++) bytes[MapHeaderSize + i] = Volatile.Read(ref _have[i]) ? (byte)1 : (byte)0;

                // Was eine andere Instanz bereits als geladen gemeldet hat, nicht wieder verlieren.
                if (File.Exists(_mapPath))
                {
                    var existing = File.ReadAllBytes(_mapPath);
                    if (existing.Length == bytes.Length && existing.AsSpan(0, MapHeaderSize).SequenceEqual(bytes.AsSpan(0, MapHeaderSize)))
                        for (var i = MapHeaderSize; i < bytes.Length; i++) bytes[i] |= existing[i];
                }

                File.WriteAllBytes(tmp, bytes);
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Move(tmp, _mapPath, overwrite: true);
                        break;
                    }
                    catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                    {
                        // z.B. Virenscanner hält die Datei kurz offen
                        Thread.Sleep(40 * attempt);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Cache-Map konnte nicht gespeichert werden", ex);
                try { File.Delete(tmp); } catch { /* egal */ }
            }
        }
    }

    // ---------- Download ----------

    private void EnsureDownloader()
    {
        if (_disposed || IsComplete) return;
        if (_downloader is { IsCompleted: false }) return;
        _downloader = Task.Run(() => DownloadLoopAsync(_cts.Token));
    }

    private async Task DownloadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[ChunkSize];
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            var start = NextMissingChunk(_wantChunk);
            if (start < 0)
            {
                lock (_fileLock) { if (!_fileClosed) _file.Flush(); }
                SaveMap();
                Completed?.Invoke(this, EventArgs.Empty);
                return;
            }

            try
            {
                var from = (long)start * ChunkSize;
                using var resp = await _api.SendAsync(() =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, _api.Resolve(_contentPath));
                    req.Headers.Range = new RangeHeaderValue(from, null);
                    return req;
                }, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!resp.IsSuccessStatusCode)
                    throw new AbsApiException($"Download fehlgeschlagen ({(int)resp.StatusCode}).", resp.StatusCode);

                // Ohne Range-Support kommt die ganze Datei ab Byte 0.
                _rangeSupported = resp.StatusCode == HttpStatusCode.PartialContent;
                var chunk = _rangeSupported ? start : 0;
                var seenVersion = Volatile.Read(ref _wantVersion);

                await using var net = await resp.Content.ReadAsStreamAsync(ct);
                while (chunk < _chunkCount)
                {
                    var len = ChunkLength(chunk);
                    await net.ReadExactlyAsync(buffer.AsMemory(0, len), ct);
                    if (!Volatile.Read(ref _have[chunk])) WriteChunk(chunk, buffer, len);
                    chunk++;
                    failures = 0;
                    _lastError = null;

                    if (!_rangeSupported) continue;

                    // Player hat an eine andere, fehlende Stelle gesprungen → dort neu ansetzen.
                    var version = Volatile.Read(ref _wantVersion);
                    if (version != seenVersion)
                    {
                        seenVersion = version;
                        var want = _wantChunk;
                        if (!Volatile.Read(ref _have[want]) && (want < chunk || want > chunk + ContinueWindowChunks))
                            break;
                    }
                    // Nächster Block schon vorhanden → zum nächsten fehlenden springen.
                    if (chunk < _chunkCount && Volatile.Read(ref _have[chunk])) break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _lastError = ex;
                Signal();
                failures++;
                Log.Error($"Download-Fehler ({failures}) für {_contentPath}", ex);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * failures)), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private void WriteChunk(int chunk, byte[] buffer, int len)
    {
        lock (_fileLock)
        {
            if (_fileClosed) return;
            _file.Position = (long)chunk * ChunkSize;
            _file.Write(buffer, 0, len);
        }

        Volatile.Write(ref _have[chunk], true);
        var count = Interlocked.Increment(ref _haveCount);
        if (count % 32 == 0)
        {
            lock (_fileLock) { if (!_fileClosed) _file.Flush(); }
            SaveMap();
        }
        Signal();
    }

    private int NextMissingChunk(int from)
    {
        from = Math.Clamp(from, 0, Math.Max(0, _chunkCount - 1));
        for (var i = from; i < _chunkCount; i++)
            if (!Volatile.Read(ref _have[i])) return i;
        for (var i = 0; i < from; i++)
            if (!Volatile.Read(ref _have[i])) return i;
        return -1;
    }

    private int ChunkLength(int chunk)
        => (int)Math.Min(ChunkSize, Length - (long)chunk * ChunkSize);

    private void Signal()
    {
        var old = Interlocked.Exchange(ref _signal, NewSignal());
        old.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task WaitForChunkAsync(int chunk, CancellationToken ct)
    {
        var lastProgress = DateTime.UtcNow;
        var lastCount = Volatile.Read(ref _haveCount);

        while (true)
        {
            var signal = Volatile.Read(ref _signal).Task;
            if (Volatile.Read(ref _have[chunk])) return;
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_wantChunk != chunk)
            {
                _wantChunk = chunk;
                Interlocked.Increment(ref _wantVersion);
            }
            EnsureDownloader();

            var count = Volatile.Read(ref _haveCount);
            if (count != lastCount)
            {
                lastCount = count;
                lastProgress = DateTime.UtcNow;
            }

            var remaining = StallTimeout - (DateTime.UtcNow - lastProgress);
            if (remaining <= TimeSpan.Zero)
                throw new IOException("Audiodaten konnten nicht geladen werden (Zeitüberschreitung).", _lastError);

            try
            {
                await signal.WaitAsync(remaining, ct);
            }
            catch (TimeoutException)
            {
                // Schleife prüft erneut und wirft ggf. oben.
            }
        }
    }

    // ---------- Stream ----------

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var total = 0;
        while (total < buffer.Length && _position < Length)
        {
            var chunk = (int)(_position / ChunkSize);
            await WaitForChunkAsync(chunk, ct);

            var chunkEnd = Math.Min(Length, (long)(chunk + 1) * ChunkSize);
            var toRead = (int)Math.Min(buffer.Length - total, chunkEnd - _position);
            int n;
            lock (_fileLock)
            {
                ObjectDisposedException.ThrowIf(_fileClosed, this);
                _file.Position = _position;
                n = _file.Read(buffer.Span.Slice(total, toRead));
            }
            if (n <= 0) break;
            _position += n;
            total += n;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _cts.Cancel();
            Signal();

            void CloseFile()
            {
                lock (_fileLock)
                {
                    if (_fileClosed) return;
                    try { _file.Flush(); } catch { /* ignore */ }
                    _file.Dispose();
                    _fileClosed = true;
                }
                SaveMap();
                _cts.Dispose();
            }

            if (_downloader is { IsCompleted: false } d) d.ContinueWith(_ => CloseFile(), TaskScheduler.Default);
            else CloseFile();
        }
        base.Dispose(disposing);
    }
}
