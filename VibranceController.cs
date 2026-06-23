using NvAPIWrapper;
using NvAPIWrapper.Display;

namespace vibranceGUI2;

/// <summary>
/// NVAPI vibrance wrapper. Falls back gracefully on non-NVIDIA GPUs.
/// Uses NvAPIWrapper — same approach as NVCP_Toggle.
/// </summary>
public sealed class VibranceController : IDisposable
{
    private Display? _primaryDisplay;

    public bool IsAvailable { get; private set; }
    public int MinLevel => 0;
    public int MaxLevel => 100;
    public int DefaultLevel => 50;

    public VibranceController()
    {
        try
        {
            NVIDIA.Initialize();
            var displays = Display.GetDisplays();
            if (displays.Length > 0)
            {
                // Match primary display — same logic as NVCP_Toggle
                var configs = PathInfo.GetDisplaysConfig();
                for (int i = 0; i < configs.Length && i < displays.Length; i++)
                {
                    if (configs[i].IsGDIPrimary)
                    {
                        _primaryDisplay = displays[i];
                        break;
                    }
                }
                _primaryDisplay ??= displays[0];
                IsAvailable = true;
            }
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public int CurrentLevel
    {
        get
        {
            if (!IsAvailable || _primaryDisplay == null) return DefaultLevel;
            try { return _primaryDisplay.DigitalVibranceControl.CurrentLevel; }
            catch { return DefaultLevel; }
        }
        set
        {
            if (!IsAvailable || _primaryDisplay == null) return;
            var clamped = Math.Clamp(value, MinLevel, MaxLevel);
            try { _primaryDisplay.DigitalVibranceControl.CurrentLevel = clamped; }
            catch { /* NVAPI comm failure — ignore */ }
        }
    }

    public void Dispose()
    {
        if (!IsAvailable) return;
        try { CurrentLevel = DefaultLevel; }
        catch { }
    }
}
