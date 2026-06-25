using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace vibranceGUI2;

public class WinEventHookEventArgs : EventArgs
{
    public IntPtr Handle { get; init; }
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string WindowTitle { get; init; } = "";
}

/// <summary>
/// Hooks EVENT_SYSTEM_FOREGROUND to detect foreground window changes.
/// Based on vibranceGUI's WinEventHook, simplified.
/// </summary>
public sealed class WinEventHook : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    public event EventHandler<WinEventHookEventArgs>? ForegroundChanged;

    private readonly IntPtr _hookHandle;
    private readonly WinEventDelegate _callback;

    private static readonly Lazy<WinEventHook> _instance = new(() => new WinEventHook());

    public static WinEventHook Instance => _instance.Value;

    private WinEventHook()
    {
        _callback = WinEventProc;
        _hookHandle = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public void Dispose()
    {
        UnhookWinEvent(_hookHandle);
    }

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // ponytail: skip tool/overlay windows (alt-tab switcher, tooltips).
        // WS_EX_TOOLWINDOW is a static property — GetForegroundWindow() is stale with fast alt-tabs.
        if (((uint)GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return;

        GetWindowThreadProcessId(hwnd, out uint processId);
        int len = GetWindowTextLengthW(hwnd);
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);

        string processName = "";
        try
        {
            using var p = Process.GetProcessById((int)processId);
            processName = p.ProcessName;
        }
        catch
        {
            // process exited already
        }

        var args = new WinEventHookEventArgs
        {
            Handle = hwnd,
            ProcessId = processId,
            ProcessName = processName,
            WindowTitle = sb.ToString()
        };

        ForegroundChanged?.Invoke(this, args);
    }
}
