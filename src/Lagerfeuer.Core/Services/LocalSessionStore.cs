using System.Text.Json;
using Lagerfeuer.Models;

namespace Lagerfeuer.Services;

/// <summary>
/// Wiedergabe-Sessions, die ohne Server-Verbindung entstanden sind. Sie werden lokal gesammelt und
/// beim nächsten Online-Gang per POST /api/session/local-all an den Server übertragen. Der Server
/// übernimmt den Fortschritt nur, wenn die Session neuer ist als sein eigener Stand.
/// </summary>
public sealed class LocalSessionStore
{
    private readonly string _path;
    private readonly object _sync = new();
    private readonly Dictionary<string, LocalSession> _sessions = new();

    public LocalSessionStore(string path)
    {
        _path = path;
        Load();
    }

    public IReadOnlyList<LocalSession> Pending
    {
        get { lock (_sync) return _sessions.Values.OrderBy(s => s.StartedAt).ToList(); }
    }

    /// <summary>Legt eine Session an oder aktualisiert sie (gleiche Id).</summary>
    public void Upsert(LocalSession session)
    {
        lock (_sync) _sessions[session.Id] = session;
        Save();
    }

    /// <summary>
    /// Überträgt alle offenen Sessions. Erfolgreiche werden lokal entfernt, fehlgeschlagene bleiben
    /// für den nächsten Versuch. Liefert die Zahl der übertragenen Sessions.
    /// </summary>
    public async Task<int> SyncAsync(AbsApiClient api, string deviceId, CancellationToken ct = default)
    {
        var pending = Pending;
        if (pending.Count == 0) return 0;

        var results = await api.SyncLocalSessionsAsync(pending, deviceId, ct);
        var synced = 0;
        lock (_sync)
        {
            foreach (var result in results)
            {
                if (result.Success)
                {
                    // Nur entfernen, wenn sie seit dem Senden nicht weitergelaufen ist.
                    var sent = pending.FirstOrDefault(s => s.Id == result.Id);
                    if (sent is not null && _sessions.TryGetValue(result.Id, out var now) && now.UpdatedAt == sent.UpdatedAt)
                        _sessions.Remove(result.Id);
                    synced++;
                }
                else
                {
                    Log.Error($"Offline-Session {result.Id} vom Server abgelehnt: {result.Error}");
                }
            }
        }
        Save();
        return synced;
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            foreach (var s in JsonSerializer.Deserialize<List<LocalSession>>(File.ReadAllText(_path), AbsApiClient.Json) ?? [])
                _sessions[s.Id] = s;
        }
        catch (Exception ex)
        {
            Log.Error("Offline-Sessions unlesbar", ex);
        }
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_sync) json = JsonSerializer.Serialize(_sessions.Values, AbsApiClient.Json);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Offline-Sessions konnten nicht gespeichert werden", ex);
        }
    }
}
