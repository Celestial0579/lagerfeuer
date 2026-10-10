using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Lagerfeuer.Services;

/// <summary>
/// Taskleisten-Miniplayer: Steuerknöpfe im Vorschaubild (Thumbnail-Toolbar) und Hörfortschritt
/// im Taskleisten-Symbol – über ITaskbarList3 und eine Fenster-Unterklasse für die Klicks.
/// </summary>
public sealed partial class TaskbarIntegration : IDisposable
{
    private const uint WM_COMMAND = 0x0111;
    private const int THBN_CLICKED = 0x1800;

    private enum ButtonId : uint { Previous = 1, Back = 2, PlayPause = 3, Forward = 4, Next = 5 }

    private readonly nint _hwnd;
    private readonly PlayerService _player;
    private readonly DispatcherQueue _dq;
    private readonly ITaskbarList3? _taskbar;
    private readonly uint _taskbarButtonCreatedMsg;
    private readonly SubclassProc _subclassProc; // Referenz halten, sonst sammelt der GC den Delegate ein
    private readonly Dictionary<string, nint> _icons = [];
    private bool _buttonsAdded;
    private int _lastProgressPermille = -1;
    private TaskbarProgressState _lastState = (TaskbarProgressState)(-1);
    private bool? _lastPlaying;
    private bool? _lastEnabled;

    public TaskbarIntegration(nint hwnd, PlayerService player, DispatcherQueue dq)
    {
        _hwnd = hwnd;
        _player = player;
        _dq = dq;
        _taskbarButtonCreatedMsg = RegisterWindowMessage("TaskbarButtonCreated");
        _subclassProc = WndProc;

        try
        {
            _taskbar = (ITaskbarList3)new CTaskbarList();
            _taskbar.HrInit();
        }
        catch (Exception ex)
        {
            Log.Error("Taskleisten-Schnittstelle nicht verfügbar", ex);
            return;
        }

        SetWindowSubclass(_hwnd, _subclassProc, 1, 0);
        _player.PropertyChanged += OnPlayerChanged;
        TryAddButtons(); // falls die Taskleistenschaltfläche bereits existiert
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerService.Position) or nameof(PlayerService.Duration) or nameof(PlayerService.IsPlaying)
                or nameof(PlayerService.HasSession) or nameof(PlayerService.IsBuffering):
                UpdateProgress();
                UpdateButtons();
                break;
            case nameof(PlayerService.Title) or nameof(PlayerService.Author):
                UpdateTooltip();
                break;
        }
    }

    // ---------- Thumbnail-Toolbar ----------

    private void TryAddButtons()
    {
        if (_taskbar is null || _buttonsAdded) return;
        var buttons = new[]
        {
            Button(ButtonId.Previous, "previous", "Vorheriges Kapitel"),
            Button(ButtonId.Back, "back10", "10 Sekunden zurück"),
            Button(ButtonId.PlayPause, "play", "Wiedergabe"),
            Button(ButtonId.Forward, "forward30", "30 Sekunden vor"),
            Button(ButtonId.Next, "next", "Nächstes Kapitel"),
        };
        foreach (ref var b in buttons.AsSpan()) b.dwFlags = _player.HasSession ? ThumbButtonFlags.Enabled : ThumbButtonFlags.Disabled;
        try
        {
            _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
            _buttonsAdded = true;
            _lastEnabled = _player.HasSession;
            UpdateButtons();
            UpdateProgress();
        }
        catch (COMException)
        {
            // Schaltfläche existiert noch nicht → später bei "TaskbarButtonCreated"
        }
    }

    private void UpdateButtons()
    {
        if (_taskbar is null || !_buttonsAdded) return;
        var enabled = _player.HasSession;
        var playing = _player.IsPlaying;
        if (_lastEnabled == enabled && _lastPlaying == playing) return;
        _lastEnabled = enabled;
        _lastPlaying = playing;

        var flags = enabled ? ThumbButtonFlags.Enabled : ThumbButtonFlags.Disabled;
        var buttons = new[]
        {
            Button(ButtonId.Previous, "previous", "Vorheriges Kapitel", flags),
            Button(ButtonId.Back, "back10", "10 Sekunden zurück", flags),
            Button(ButtonId.PlayPause, playing ? "pause" : "play", playing ? "Pause" : "Wiedergabe", flags),
            Button(ButtonId.Forward, "forward30", "30 Sekunden vor", flags),
            Button(ButtonId.Next, "next", "Nächstes Kapitel", flags),
        };
        try { _taskbar.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons); }
        catch (COMException ex) { Log.Error("Taskleistenknöpfe konnten nicht aktualisiert werden", ex); }
    }

    private ThumbButton Button(ButtonId id, string icon, string tooltip, ThumbButtonFlags flags = ThumbButtonFlags.Enabled) => new()
    {
        dwMask = ThumbButtonMask.Icon | ThumbButtonMask.Tooltip | ThumbButtonMask.Flags,
        iId = (uint)id,
        hIcon = LoadIcon(icon),
        szTip = tooltip,
        dwFlags = flags,
    };

    private nint LoadIcon(string name)
    {
        if (_icons.TryGetValue(name, out var icon)) return icon;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Taskbar", name + ".ico");
        var size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForWindow(_hwnd));
        icon = LoadImage(0, path, IMAGE_ICON, size, size, LR_LOADFROMFILE);
        _icons[name] = icon;
        return icon;
    }

    private void OnButtonClicked(ButtonId id)
    {
        switch (id)
        {
            case ButtonId.Previous: _player.PreviousChapter(); break;
            case ButtonId.Back: _ = _player.SkipAsync(-10); break;
            case ButtonId.PlayPause: _player.TogglePlayPause(); break;
            case ButtonId.Forward: _ = _player.SkipAsync(30); break;
            case ButtonId.Next: _player.NextChapter(); break;
        }
    }

    // ---------- Fortschritt & Tooltip ----------

    private void UpdateProgress()
    {
        if (_taskbar is null) return;
        var state = !_player.HasSession ? TaskbarProgressState.NoProgress
            : _player.IsBuffering ? TaskbarProgressState.Indeterminate
            : _player.IsPlaying ? TaskbarProgressState.Normal
            : TaskbarProgressState.Paused;
        try
        {
            if (state != _lastState)
            {
                _taskbar.SetProgressState(_hwnd, state);
                _lastState = state;
                _lastProgressPermille = -1;
            }
            if (state is TaskbarProgressState.Normal or TaskbarProgressState.Paused && _player.Duration > 0)
            {
                var permille = (int)(1000 * Math.Clamp(_player.Position / _player.Duration, 0, 1));
                if (permille != _lastProgressPermille)
                {
                    _taskbar.SetProgressValue(_hwnd, (ulong)permille, 1000);
                    _lastProgressPermille = permille;
                }
            }
        }
        catch (COMException ex)
        {
            Log.Error("Taskleistenfortschritt konnte nicht gesetzt werden", ex);
        }
    }

    private void UpdateTooltip()
    {
        if (_taskbar is null) return;
        var tip = _player.HasSession && !string.IsNullOrEmpty(_player.Title)
            ? $"{_player.Title}{(string.IsNullOrEmpty(_player.Author) ? "" : " – " + _player.Author)}"
            : null;
        try { _taskbar.SetThumbnailTooltip(_hwnd, tip); }
        catch (COMException) { /* nicht kritisch */ }
    }

    // ---------- Fensternachrichten ----------

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
    {
        if (msg == WM_COMMAND && ((wParam.ToInt64() >> 16) & 0xFFFF) == THBN_CLICKED)
        {
            var button = (ButtonId)(wParam.ToInt64() & 0xFFFF);
            _dq.TryEnqueue(() => OnButtonClicked(button));
            return 0;
        }
        if (msg == _taskbarButtonCreatedMsg)
        {
            // Explorer neu gestartet oder Schaltfläche neu erstellt → Knöpfe erneut anlegen
            _buttonsAdded = false;
            _lastState = (TaskbarProgressState)(-1);
            _lastPlaying = _lastEnabled = null;
            _dq.TryEnqueue(TryAddButtons);
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _player.PropertyChanged -= OnPlayerChanged;
        if (_taskbar is not null) RemoveWindowSubclass(_hwnd, _subclassProc, 1);
        foreach (var icon in _icons.Values) DestroyIcon(icon);
        _icons.Clear();
    }

    // ---------- Interop ----------

    private enum TaskbarProgressState { NoProgress = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

    [Flags]
    private enum ThumbButtonMask : uint { Bitmap = 1, Icon = 2, Tooltip = 4, Flags = 8 }

    [Flags]
    private enum ThumbButtonFlags : uint { Enabled = 0, Disabled = 1, DismissionClick = 2, NoBackground = 4, Hidden = 8 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ThumbButton
    {
        public ThumbButtonMask dwMask;
        public uint iId;
        public uint iBitmap;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szTip;
        public ThumbButtonFlags dwFlags;
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class CTaskbarList;

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, TaskbarProgressState state);
        void RegisterTab(nint hwndTab, nint hwndMdi);
        void UnregisterTab(nint hwndTab);
        void SetTabOrder(nint hwndTab, nint hwndInsertBefore);
        void SetTabActive(nint hwndTab, nint hwndMdi, uint reserved);
        void ThumbBarAddButtons(nint hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] ThumbButton[] buttons);
        void ThumbBarUpdateButtons(nint hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] ThumbButton[] buttons);
        void ThumbBarSetImageList(nint hwnd, nint imageList);
        void SetOverlayIcon(nint hwnd, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
        void SetThumbnailTooltip(nint hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? tip);
        void SetThumbnailClip(nint hwnd, nint clip);
    }

    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData);

    private const int SM_CXSMICON = 49;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint refData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint load);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);
}
