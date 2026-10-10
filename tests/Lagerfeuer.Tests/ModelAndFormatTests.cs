using System.Text.Json;
using Lagerfeuer.Models;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>Deserialisierung typischer Audiobookshelf-Antworten (Format wie ABS 2.37).</summary>
public class ModelTests
{
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, AbsApiClient.Json)!;

    [Fact]
    public void Status_ErkenntAnmeldemethoden()
    {
        var status = Parse<ServerStatus>("""
            {"app":"audiobookshelf","serverVersion":"2.37.1","isInit":true,
             "authMethods":["local","openid"],
             "authFormData":{"authLoginCustomMessage":null,"authOpenIDButtonText":"Mit Authentik anmelden","authOpenIDAutoLaunch":false}}
            """);

        Assert.True(status.IsInit);
        Assert.True(status.SupportsLocal);
        Assert.True(status.SupportsOpenId);
        Assert.Equal("Mit Authentik anmelden", status.AuthFormData!.AuthOpenIDButtonText);
    }

    [Fact]
    public void Status_NurOpenId()
    {
        var status = Parse<ServerStatus>("""{"isInit":true,"authMethods":["openid"]}""");
        Assert.False(status.SupportsLocal);
        Assert.True(status.SupportsOpenId);
    }

    [Fact]
    public void Login_MitTokensUndFortschritt()
    {
        var login = Parse<LoginResponse>("""
            {"user":{"id":"u1","username":"anna","accessToken":"a","refreshToken":"r","token":"legacy",
              "mediaProgress":[{"id":"p1","libraryItemId":"i1","episodeId":null,"duration":3600.5,"progress":0.25,
                                "currentTime":900.125,"isFinished":false,"lastUpdate":1760000000000}]},
             "userDefaultLibraryId":"lib1","serverSettings":{"id":"server-settings"}}
            """);

        Assert.Equal("a", login.User.AccessToken);
        Assert.Equal("r", login.User.RefreshToken);
        var progress = Assert.Single(login.User.MediaProgress);
        Assert.Equal(900.125, progress.CurrentTime);
        Assert.Equal(1760000000000, progress.LastUpdate);
    }

    [Fact]
    public void PlaybackSession_MitTracksUndKapiteln()
    {
        var session = Parse<PlaybackSession>("""
            {"id":"sess","libraryItemId":"item","displayTitle":"Das Buch","displayAuthor":"Autorin","duration":7200,
             "currentTime":1234.5,"playMethod":0,
             "audioTracks":[
               {"index":1,"startOffset":0,"duration":3600,"title":"Teil 1","contentUrl":"/api/items/item/file/11",
                "mimeType":"audio/mpeg","metadata":{"filename":"teil1.mp3","ext":".mp3","size":52428800}},
               {"index":2,"startOffset":3600,"duration":3600,"title":"Teil 2","contentUrl":"/api/items/item/file/12",
                "mimeType":"audio/mpeg","metadata":{"filename":"teil2.mp3","ext":".mp3","size":52428800}}],
             "chapters":[{"id":0,"start":0,"end":1800,"title":"Kapitel 1"},{"id":1,"start":1800,"end":7200,"title":"Kapitel 2"}]}
            """);

        Assert.Equal(2, session.AudioTracks.Count);
        Assert.Equal(3600, session.AudioTracks[1].StartOffset);
        Assert.Equal(".mp3", session.AudioTracks[0].Metadata!.Ext);
        Assert.Equal("Kapitel 2", session.Chapters[1].Title);
    }

    [Fact]
    public void Buch_AnzeigeAutorUndSprecher()
    {
        var expanded = Parse<LibraryItem>("""
            {"id":"i","libraryId":"l","mediaType":"book","media":{"duration":100,
              "metadata":{"title":"T","authors":[{"id":"a1","name":"Erste"},{"id":"a2","name":"Zweite"}],"narrators":["Sprecherin"]},
              "audioFiles":[{"index":1}],"chapters":[]}}
            """);
        var minified = Parse<LibraryItem>("""{"id":"i","media":{"metadata":{"title":"T","authorName":"Erste, Zweite","narratorName":"Sprecherin"}}}""");
        var ebook = Parse<LibraryItem>("""{"id":"i","media":{"metadata":{"title":"T"},"audioFiles":[]}}""");

        Assert.Equal("Erste, Zweite", expanded.Media.Metadata.DisplayAuthor);
        Assert.Equal("Sprecherin", expanded.Media.Metadata.DisplayNarrator);
        Assert.Equal("Erste, Zweite", minified.Media.Metadata.DisplayAuthor);
        Assert.Single(expanded.Media.AudioFiles!);
        Assert.Empty(ebook.Media.AudioFiles!);
        Assert.Null(minified.Media.AudioFiles); // in der Listenansicht nicht enthalten
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59.9, "0:59")]
    [InlineData(61, "1:01")]
    [InlineData(3600, "1:00:00")]
    [InlineData(36000 + 62, "10:01:02")]
    [InlineData(-5, "0:00")]
    [InlineData(double.NaN, "0:00")]
    public void Time(double seconds, string expected) => Assert.Equal(expected, Format.Time(seconds));

    [Theory]
    [InlineData(59 * 60, "59 Min.")]
    [InlineData(3 * 3600 + 25 * 60, "3 Std. 25 Min.")]
    public void Duration(double seconds, string expected) => Assert.Equal(expected, Format.Duration(seconds));

    [Fact]
    public void StripHtml_EntferntTagsUndDekodiertEntities()
        => Assert.Equal("Erster Absatz\nZweiter & letzter",
            Format.StripHtml("<p>Erster <b>Absatz</b></p><p>Zweiter &amp; letzter</p>"));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void StripHtml_LeererText(string? html) => Assert.Equal("", Format.StripHtml(html));
}
