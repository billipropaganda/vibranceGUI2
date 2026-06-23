using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace vibranceGUI2;

/// <summary>
/// Resolution / refresh rate / DPI scale switching via ChangeDisplaySettingsEx + registry.
/// Ported from the working display-switcher.ps1 PowerShell script.
/// </summary>
public sealed class DisplayMode
{
    // ── DEVMODE ──────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    // ── P/Invoke (IntPtr for device name — matches working PS script) ─

    private const uint DM_PELSWIDTH = 0x80000;
    private const uint DM_PELSHEIGHT = 0x100000;
    private const uint DM_DISPLAYFREQUENCY = 0x400000;
    private const uint DISPLAY_DEVICE_ATTACHED = 0x01;
    private const uint DISPLAY_DEVICE_PRIMARY = 0x04;
    private const int CDS_UPDATEREGISTRY = 0x01;
    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumDisplayDevices(IntPtr lpDevice, uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumDisplaySettings(IntPtr lpszDeviceName, int iModeNum,
        ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(IntPtr lpszDeviceName,
        ref DEVMODE lpDevMode, IntPtr hwnd, uint dwFlags, IntPtr lParam);

    // ── String→IntPtr helper ───────────────────────────────

    private static IntPtr ToPtr(string? s)
        => s == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(s);

    // ── Public API ───────────────────────────────────────────

    public readonly record struct ModeInfo(uint Width, uint Height, uint RefreshRate);

    /// <summary>Get the current mode for the primary display device.</summary>
    public static ModeInfo GetCurrentMode()
    {
        var deviceName = GetPrimaryDeviceName();
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        var ptr = ToPtr(deviceName);
        try
        {
            if (EnumDisplaySettings(ptr, ENUM_CURRENT_SETTINGS, ref dm) == 0)
                return default;
            return new ModeInfo(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>Apply a display mode (resolution + refresh rate) to the primary monitor.</summary>
    public static bool SetMode(uint width, uint height, uint refreshRate)
    {
        var deviceName = GetPrimaryDeviceName();
        var dm = new DEVMODE
        {
            dmSize = (ushort)Marshal.SizeOf<DEVMODE>(),
            dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY,
            dmPelsWidth = width,
            dmPelsHeight = height,
            dmDisplayFrequency = refreshRate
        };
        var ptr = ToPtr(deviceName);
        try
        {
            return ChangeDisplaySettingsEx(ptr, ref dm, IntPtr.Zero,
                CDS_UPDATEREGISTRY, IntPtr.Zero) == 0;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>Get the primary monitor's EDID string from the registry.</summary>
    public static string? GetPrimaryEDID()
    {
        string? fallbackEdid = null;
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(IntPtr.Zero, i, ref dd, 0) == 0) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED) == 0) continue;

            var monitor = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            var devPtr = ToPtr(dd.DeviceName);
            try
            {
                EnumDisplayDevices(devPtr, 0, ref monitor, 0);
            }
            finally { Marshal.FreeHGlobal(devPtr); }

            if (monitor.DeviceID != null && TryExtractEDID(monitor.DeviceID, out var edid))
            {
                if ((dd.StateFlags & DISPLAY_DEVICE_PRIMARY) != 0)
                    return edid; // primary wins
                fallbackEdid ??= edid;
            }
        }
        return fallbackEdid;
    }

    private static string? GetPrimaryDeviceName()
    {
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(IntPtr.Zero, i, ref dd, 0) == 0) break;
            // Must be attached AND primary (flag 0x01 | 0x04)
            if ((dd.StateFlags & (DISPLAY_DEVICE_ATTACHED | DISPLAY_DEVICE_PRIMARY)) ==
                (DISPLAY_DEVICE_ATTACHED | DISPLAY_DEVICE_PRIMARY))
                return dd.DeviceName;
        }
        // Fallback: first attached device
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(IntPtr.Zero, i, ref dd, 0) == 0) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED) != 0)
                return dd.DeviceName;
        }
        return null;
    }

    private static bool TryExtractEDID(string deviceID, out string edid)
    {
        edid = "";
        foreach (var sep in new[] { "MONITOR\\", "DISPLAY\\" })
        {
            var idx = deviceID.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var end = deviceID.IndexOf('\\', idx + sep.Length);
                edid = end >= 0
                    ? deviceID.Substring(idx + sep.Length, end - idx - sep.Length)
                    : deviceID.Substring(idx + sep.Length);
                return true;
            }
        }
        return false;
    }

    /// <summary>Set per-monitor DPI scale via registry. Requires no admin.</summary>
    public static void SetDpiScale(string edid, int percent)
    {
        int dpiVal = percent switch
        {
            100 => 0, 125 => 1, 150 => 2, 175 => 3, 200 => 4, _ => 0
        };

        // HKCU — takes effect immediately with mode re-apply
        using var hkcu = Registry.CurrentUser.OpenSubKey(
            @"Control Panel\Desktop\PerMonitorSettings", writable: true);
        if (hkcu != null)
        {
            foreach (var sub in hkcu.GetSubKeyNames())
            {
                if (sub.Contains(edid, StringComparison.OrdinalIgnoreCase))
                {
                    using var k = hkcu.OpenSubKey(sub, writable: true);
                    k?.SetValue("DpiValue", dpiVal, RegistryValueKind.DWord);
                    break;
                }
            }
        }

        // HKLM fallback
        try
        {
            using var hklm = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\ScaleFactors", writable: true);
            if (hklm != null)
            {
                foreach (var sub in hklm.GetSubKeyNames())
                {
                    if (sub.Contains(edid, StringComparison.OrdinalIgnoreCase))
                    {
                        using var k = hklm.OpenSubKey(sub, writable: true);
                        k?.SetValue("DpiValue", dpiVal, RegistryValueKind.DWord);
                        break;
                    }
                }
            }
        }
        catch { /* HKLM may need admin */ }
    }

    /// <summary>Enumerate all available display modes for the primary monitor.</summary>
    public static List<ModeInfo> GetAvailableModes()
    {
        var deviceName = GetPrimaryDeviceName();
        var seen = new HashSet<string>();
        var modes = new List<ModeInfo>();
        var ptr = ToPtr(deviceName);
        try
        {
            for (int i = 0; ; i++)
            {
                var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettings(ptr, i, ref dm) == 0) break;
                var key = $"{dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency}";
                if (seen.Add(key))
                    modes.Add(new ModeInfo(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }

        modes.Sort((a, b) =>
        {
            int c = b.Width.CompareTo(a.Width);
            if (c != 0) return c;
            return b.RefreshRate.CompareTo(a.RefreshRate);
        });
        return modes;
    }
}
