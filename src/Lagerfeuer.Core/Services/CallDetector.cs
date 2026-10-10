using System.Runtime.InteropServices;

namespace Lagerfeuer.Services;

/// <summary>
/// Erkennt Anrufe (Teams, Zoom, Telefon-Link …) über die Ducking-Benachrichtigungen von Windows:
/// Öffnet eine App einen Kommunikations-Audiostream, meldet WASAPI das allen registrierten
/// Empfängern (IAudioVolumeDuckNotification), ebenso das Ende.
///
/// Hinweis: Windows verschickt diese Meldungen nur, wenn unter Sound → Kommunikation nicht
/// „Nichts unternehmen“ eingestellt ist (Standard ist „Lautstärke um 80 % reduzieren“).
/// </summary>
public sealed class CallDetector : IDisposable
{
    private IAudioSessionManager2? _manager;
    private DuckNotificationSink? _sink;

    /// <summary>Ein Anruf hat begonnen (Hintergrund-Thread).</summary>
    public event EventHandler? CallStarted;

    /// <summary>Der Anruf ist beendet (Hintergrund-Thread).</summary>
    public event EventHandler? CallEnded;

    public bool IsRunning => _sink is not null;

    /// <summary>Registriert sich beim Standard-Wiedergabegerät. Liefert false, wenn das nicht möglich ist.</summary>
    public async Task<bool> StartAsync()
    {
        if (IsRunning) return true;
        // WASAPI-Aufrufe aus einem MTA-Thread (Threadpool), nicht aus dem UI-Thread.
        return await Task.Run(() =>
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device));
                var iid = typeof(IAudioSessionManager2).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsCtxAll, 0, out var managerObject));
                var manager = (IAudioSessionManager2)managerObject;

                var sink = new DuckNotificationSink(this);
                // sessionID = null → alle Ducking-Meldungen empfangen, ohne eigene Streams zu verändern
                Marshal.ThrowExceptionForHR(manager.RegisterDuckNotification(null, sink));
                _manager = manager;
                _sink = sink;
                Log.Info("Anruferkennung aktiv.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Anruferkennung konnte nicht gestartet werden", ex);
                return false;
            }
        });
    }

    public void Stop()
    {
        if (_manager is not null && _sink is not null)
        {
            try { _manager.UnregisterDuckNotification(_sink); }
            catch (Exception ex) { Log.Error("Anruferkennung konnte nicht abgemeldet werden", ex); }
        }
        _manager = null;
        _sink = null;
    }

    public void Dispose() => Stop();

    private void RaiseStarted() => CallStarted?.Invoke(this, EventArgs.Empty);
    private void RaiseEnded() => CallEnded?.Invoke(this, EventArgs.Empty);

    // ---------- COM-Interop (Core Audio) ----------

    private const int ClsCtxAll = 0x17;

    private enum EDataFlow { Render = 0, Capture = 1, All = 2 }

    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class DuckNotificationSink(CallDetector owner) : IAudioVolumeDuckNotification
    {
        public int OnVolumeDuckNotification(string sessionId, uint countCommunicationSessions)
        {
            owner.RaiseStarted();
            return 0;
        }

        public int OnVolumeUnduckNotification(string sessionId)
        {
            owner.RaiseEnded();
            return 0;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(nint sessionGuid, uint streamFlags, out nint sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(nint sessionGuid, uint streamFlags, out nint audioVolume);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out nint sessionEnum);
        [PreserveSig] int RegisterSessionNotification(nint sessionNotification);
        [PreserveSig] int UnregisterSessionNotification(nint sessionNotification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string? sessionId, IAudioVolumeDuckNotification notification);
        [PreserveSig] int UnregisterDuckNotification(IAudioVolumeDuckNotification notification);
    }

    [ComImport, Guid("C3B284D4-6D39-4359-B3CF-B56DDB3BB39C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioVolumeDuckNotification
    {
        [PreserveSig] int OnVolumeDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, uint countCommunicationSessions);
        [PreserveSig] int OnVolumeUnduckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId);
    }
}

/// <summary>
/// Entscheidet, was bei Anrufen passiert: laufende Wiedergabe pausieren und danach – falls gewünscht
/// und der Benutzer nicht selbst eingegriffen hat – fortsetzen.
/// </summary>
public sealed class CallPauseController(Func<bool> isPlaying, Action pause, Action play, Func<bool> autoPause, Func<bool> autoResume)
{
    private bool _pausedByCall;

    /// <summary>Hat Lagerfeuer wegen eines Anrufs pausiert?</summary>
    public bool PausedByCall => _pausedByCall;

    public void OnCallStarted()
    {
        if (!autoPause() || !isPlaying()) return;
        pause();
        _pausedByCall = true;
    }

    public void OnCallEnded()
    {
        if (!_pausedByCall) return;
        _pausedByCall = false;
        if (autoResume() && !isPlaying()) play();
    }

    /// <summary>Benutzer hat selbst Play/Pause gedrückt – dann nach dem Anruf nichts automatisch tun.</summary>
    public void OnUserAction() => _pausedByCall = false;
}
