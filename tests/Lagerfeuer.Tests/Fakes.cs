using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Lagerfeuer.Tests;

/// <summary>HTTP-Handler, der Anfragen an eine Funktion weiterreicht und protokolliert.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this(r => Task.FromResult(respond(r))) { }

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public int Count(Func<HttpRequestMessage, bool> predicate) => Requests.Count(predicate);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        return respond(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("") };
}

/// <summary>
/// Liefert eine Datei wie der Audiobookshelf-Server (Express sendFile): mit HTTP-Range (206) oder,
/// wenn abgeschaltet, immer komplett (200). Prüft das Bearer-Token.
/// </summary>
internal sealed class RangeFileServer
{
    private readonly byte[] _data;

    public RangeFileServer(byte[] data, bool rangeSupported = true, string token = "token")
    {
        _data = data;
        RangeSupported = rangeSupported;
        Token = token;
        Handler = new FakeHandler(Serve);
    }

    public FakeHandler Handler { get; }
    public bool RangeSupported { get; set; }
    public string Token { get; }
    public int FileRequests => Handler.Count(_ => true);

    private HttpResponseMessage Serve(HttpRequestMessage request)
    {
        if (request.Headers.Authorization?.Parameter != Token) return FakeHandler.Status(HttpStatusCode.Unauthorized);

        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        if (!RangeSupported || range is null)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_data) };
        }

        var from = range.From ?? 0;
        var to = Math.Min(range.To ?? _data.Length - 1, _data.Length - 1);
        var content = new ByteArrayContent(_data, (int)from, (int)(to - from + 1));
        content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, _data.Length);
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
    }
}

/// <summary>Temporäres Verzeichnis, das nach dem Test gelöscht wird.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lagerfeuer-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { /* Datei evtl. noch offen – egal */ }
    }
}
