using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace vibranceGUI2;

public class AppProfile
{
    public string ProcessName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Vibrance { get; set; } = 50;
    public double Brightness { get; set; } = 0.50;
    public double Contrast { get; set; } = 0.50;
    public double Gamma { get; set; } = 1.0;

    /// <summary>Base64-encoded PNG for persistence.</summary>
    public string IconBase64 { get; set; } = "";

    /// <summary>Runtime icon (not serialized). Set via LoadIcon or RestoreIcon.</summary>
    [JsonIgnore]
    public ImageSource? Icon { get; set; }

    /// <summary>Extract icon from an exe and serialize to base64.</summary>
    public void LoadIcon(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return;
                Icon = IconToSource(icon);
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                IconBase64 = Convert.ToBase64String(ms.ToArray());
            }
        }
        catch { }
    }

    /// <summary>Restore icon from persisted base64.</summary>
    public void RestoreIcon()
    {
        try
        {
            if (string.IsNullOrEmpty(IconBase64)) return;
            var bytes = Convert.FromBase64String(IconBase64);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.DecodePixelWidth = 16;
            bmp.EndInit();
            bmp.Freeze();
            Icon = bmp;
        }
        catch { }
    }

    private static BitmapSource IconToSource(System.Drawing.Icon icon)
    {
        using var bmp = icon.ToBitmap();
        var hbmp = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                hbmp, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DeleteObject(hbmp); }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);
}

public class AppSettings
{
    public int DefaultVibrance { get; set; } = 50;
    public double DefaultBrightness { get; set; } = 0.50;
    public double DefaultContrast { get; set; } = 0.50;
    public double DefaultGamma { get; set; } = 1.0;
    public string ThemeMode { get; set; } = "Dark";
    public bool AutoStart { get; set; }
    public ObservableCollection<AppProfile> Profiles { get; set; } = new();
}
