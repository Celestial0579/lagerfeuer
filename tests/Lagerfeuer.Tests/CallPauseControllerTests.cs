using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

public class CallPauseControllerTests
{
    private bool _playing;
    private bool _autoPause = true;
    private bool _autoResume = true;
    private int _pauses;
    private int _plays;

    private CallPauseController Create() => new(
        () => _playing,
        () => { _playing = false; _pauses++; },
        () => { _playing = true; _plays++; },
        () => _autoPause,
        () => _autoResume);

    [Fact]
    public void Anruf_PausiertUndSetztDanachFort()
    {
        _playing = true;
        var calls = Create();

        calls.OnCallStarted();
        Assert.False(_playing);
        Assert.True(calls.PausedByCall);

        calls.OnCallEnded();
        Assert.True(_playing);
        Assert.Equal((1, 1), (_pauses, _plays));
    }

    [Fact]
    public void OhneWiedergabe_PassiertNichts()
    {
        var calls = Create();
        calls.OnCallStarted();
        calls.OnCallEnded();
        Assert.Equal((0, 0), (_pauses, _plays));
    }

    [Fact]
    public void Abgeschaltet_PausiertNicht()
    {
        _playing = true;
        _autoPause = false;
        var calls = Create();

        calls.OnCallStarted();

        Assert.True(_playing);
        Assert.Equal(0, _pauses);
    }

    [Fact]
    public void OhneFortsetzen_BleibtNachDemAnrufPausiert()
    {
        _playing = true;
        _autoResume = false;
        var calls = Create();

        calls.OnCallStarted();
        calls.OnCallEnded();

        Assert.False(_playing);
        Assert.False(calls.PausedByCall);
    }

    [Fact]
    public void BenutzerGreiftEin_DannKeinAutomatischesFortsetzen()
    {
        _playing = true;
        var calls = Create();

        calls.OnCallStarted();
        calls.OnUserAction(); // z.B. bewusst pausiert gelassen oder anderes Buch gestartet
        calls.OnCallEnded();

        Assert.Equal(0, _plays);
    }

    [Fact]
    public void MehrereAnrufmeldungen_PausierenNurEinmal()
    {
        _playing = true;
        var calls = Create();

        calls.OnCallStarted();
        calls.OnCallStarted(); // zweiter Kommunikations-Stream
        calls.OnCallEnded();

        Assert.Equal((1, 1), (_pauses, _plays));
    }
}
