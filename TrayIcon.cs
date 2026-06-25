using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace vibranceGUI2;

/// <summary>
/// Minimal system tray icon via Shell_NotifyIcon P/Invoke.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int NIM_ADD = 0, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_USER = 0x0400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    private readonly IntPtr _hIcon;
    private readonly Icon? _icon; // ponytail: keep alive so HICON isn't destroyed
    private readonly IntPtr _hwnd;
    private HwndSource? _source;
    private readonly int _callbackMsg;

    public event Action? LeftDoubleClick;
    public event Action? RightClick;

    public TrayIcon(Window window, string tooltip = "vibranceGUI2")
    {
        // ponytail: pull icon from exe (embedded via csproj ApplicationIcon)
        _icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        _hIcon = _icon?.Handle ?? IntPtr.Zero;

        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _callbackMsg = WM_USER + 1;
        _source!.AddHook(WndProc);

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = _callbackMsg,
            hIcon = _hIcon,
            szTip = tooltip
        };
        Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _callbackMsg)
        {
            int lParamVal = lParam.ToInt32() & 0xFFFF;
            if (lParamVal == WM_LBUTTONDBLCLK)
                LeftDoubleClick?.Invoke();
            else if (lParamVal == WM_RBUTTONUP)
                RightClick?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1
        };
        Shell_NotifyIcon(NIM_DELETE, ref nid);
        if (_hIcon != IntPtr.Zero)
            _icon?.Dispose();
    }

}
