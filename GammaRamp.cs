using System.Runtime.InteropServices;

namespace vibranceGUI2;

/// <summary>
/// P/Invoke wrapper for GetDeviceGammaRamp / SetDeviceGammaRamp.
/// Works on all GPUs (NVIDIA, AMD, Intel) — no driver-specific API needed.
/// </summary>
public static class GammaRamp
{
    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref GammaRampData lpRamp);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int UserReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDCW(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        // padding fields required for correct marshalling
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        private readonly string _deviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        private readonly string _deviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        private readonly string _deviceKey;
    }

    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct GammaRampData
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Blue;

        public GammaRampData(bool _)
        {
            Red = new ushort[256];
            Green = new ushort[256];
            Blue = new ushort[256];
        }
    }

    private static GammaRampData IdentityRamp
    {
        get
        {
            var r = new GammaRampData(true);
            for (int i = 0; i < 256; i++)
            {
                r.Red[i] = r.Green[i] = r.Blue[i] = (ushort)(i * 257);
            }
            return r;
        }
    }

    /// <summary>Get DC for the primary monitor by enumerating real display devices.
    /// Mirrors WindowsDisplayAPI: enum devices → find primary → CreateDC with device name.
    /// Without a real device name, NVIDIA drivers silently reject SetDeviceGammaRamp.</summary>
    public static IntPtr GetPrimaryDC()
    {
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref dd, 0))
                break;

            if ((dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0)
                return CreateDCW(null, dd.DeviceName, null, IntPtr.Zero);
        }

        // Fallback: desktop DC (last resort)
        return GetDC(IntPtr.Zero);
    }

    public static void FreeDC(IntPtr hDC)
    {
        if (hDC == IntPtr.Zero) return;
        // Try DeleteDC first (for DCs created with CreateDCW).
        // Fall back to ReleaseDC (for DCs obtained with GetDC).
        if (!DeleteDC(hDC))
            UserReleaseDC(IntPtr.Zero, hDC);
    }

    /// <summary>Compute gamma ramp from brightness (0-1), contrast (0-1), gamma.
    /// Uses the exact same algorithm as WindowsDisplayAPI (NVCP Toggle).</summary>
    public static void ComputeRamp(double brightness, double contrast, double gamma,
        out ushort[] red, out ushort[] green, out ushort[] blue)
    {
        red = CalculateLUT(brightness, contrast, gamma);
        green = CalculateLUT(brightness, contrast, gamma);
        blue = CalculateLUT(brightness, contrast, gamma);
    }

    /// <summary>Exact WindowsDisplayAPI CalculateLUT implementation.</summary>
    private static ushort[] CalculateLUT(double brightness, double contrast, double gamma)
    {
        // Limit gamma in range [0.4-2.8]
        gamma = Math.Max(Math.Min(gamma, 2.8), 0.4);

        // Normalize contrast and brightness from [0,1] to [-1,1]
        contrast = (Math.Max(Math.Min(contrast, 1.0), 0.0) - 0.5) * 2.0;
        brightness = (Math.Max(Math.Min(brightness, 1.0), 0.0) - 0.5) * 2.0;

        // Calculate curve offset resulted from contrast
        var offset = contrast > 0 ? contrast * -25.4 : contrast * -32.0;

        // Calculate the total range of curve
        var range = 255.0 + offset * 2.0; // DataPoints - 1 = 255

        // Add brightness to the curve offset
        offset += brightness * (range / 5.0);

        var result = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            var factor = (i + offset) / range;
            factor = Math.Pow(factor, 1.0 / gamma);
            factor = Math.Max(Math.Min(factor, 1.0), 0.0);
            result[i] = (ushort)Math.Round(factor * ushort.MaxValue);
        }
        return result;
    }

    /// <summary>Apply brightness/contrast/gamma to a monitor DC.</summary>
    public static bool Apply(IntPtr hDC, double brightness, double contrast, double gamma)
    {
        ComputeRamp(brightness, contrast, gamma, out var r, out var g, out var b);
        var ramp = new GammaRampData(true) { Red = r, Green = g, Blue = b };
        return SetDeviceGammaRamp(hDC, ref ramp);
    }

    /// <summary>Reset a monitor DC to linear/identity ramp.</summary>
    public static bool Reset(IntPtr hDC)
    {
        var ramp = IdentityRamp;
        return SetDeviceGammaRamp(hDC, ref ramp);
    }

}
