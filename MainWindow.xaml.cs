using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

using Microsoft.Win32;
using TextBox = System.Windows.Controls.TextBox;

namespace vibranceGUI2;

public partial class MainWindow : Window
{
    private AppSettings _settings = new();
    private AppProfile? _activeProfile;
    private bool _isDefaultActive = true;
    private IntPtr _monitorDC;
    private readonly VibranceController _vibrance = new();

    private readonly TrayIcon _trayIcon;
    private readonly ContextMenu _trayMenu;
    private readonly string _settingsPath;
    private const string AppName = "vibranceGUI2";
    private const string AutoStartKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault
    };

    public MainWindow()
    {
        InitializeComponent();
        // ponytail: dark title bar needs the HWND, attach once it exists
        SourceInitialized += (_, _) => ThemeWindow(this);

        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppName, "settings.json");

        _trayIcon = new TrayIcon(this, AppName);
        _trayIcon.LeftDoubleClick += ShowWindow;
        _trayIcon.RightClick += ShowTrayMenu;

        _trayMenu = new ContextMenu();
        // ponytail: implicit styles from Controls.xaml handle Background/Foreground/Border
        var showItem = new MenuItem { Header = "Show" };
        showItem.Click += (_, _) => ShowWindow();
        var settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += (_, _) => { ShowWindow(); SettingsBtn_Click(this, new RoutedEventArgs()); };
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => CloseApp();
        _trayMenu.Items.Add(showItem);
        _trayMenu.Items.Add(settingsItem);
        _trayMenu.Items.Add(exitItem);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _monitorDC = GammaRamp.GetPrimaryDC();

        LoadSettings();

        if (!_vibrance.IsAvailable)
        {
            DefaultVibrance.IsEnabled = false;
            DefaultVibranceText.IsEnabled = false;
        }
        DefaultVibrance.Value = _settings.DefaultVibrance;
        DefaultBrightness.Value = _settings.DefaultBrightness;
        DefaultContrast.Value = _settings.DefaultContrast;
        DefaultGamma.Value = _settings.DefaultGamma;
        UpdateTextBoxes();

        foreach (var p in _settings.Profiles)
        {
            p.RestoreIcon();
            if (p.Icon != null) continue;

            // Load from known exe path
            if (!string.IsNullOrEmpty(p.FilePath) && File.Exists(p.FilePath))
                p.LoadIcon(p.FilePath);

            // ponytail: fallback — find process by name, get its exe path
            if (p.Icon == null)
            {
                try
                {
                    var procs = Process.GetProcessesByName(p.ProcessName);
                    if (procs.Length > 0)
                    {
                        var exePath = ProcessPickerWindow.GetProcessPath(procs[0]);
                        if (!string.IsNullOrEmpty(exePath)) { p.FilePath = exePath; p.LoadIcon(exePath); }
                    }
                }
                catch { }
            }
        }

        RefreshCardGrid();
        UpdateEmptyState();

        // Need to track count changes from outside since ItemsControl doesn't auto-detect
        _settings.Profiles.CollectionChanged += (_, _) =>
        {
            Dispatcher.Invoke(() => { RefreshCardGrid(); UpdateEmptyState(); });
        };

        WinEventHook.Instance.ForegroundChanged += OnForegroundChanged;

        ApplyTheme(_settings.ThemeMode, this);
        ApplyDefault();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    private void ShowWindow()
    {
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void ShowTrayMenu()
    {
        // Let WPF position at cursor — avoid DPI-scaled GetCursorPos issues
        _trayMenu.Placement = PlacementMode.MousePoint;
        _trayMenu.IsOpen = true;
    }

    private void CloseApp()
    {
        Cleanup();
        _trayIcon.Dispose();
        Application.Current.Shutdown();
    }

    public void Cleanup()
    {
        WinEventHook.Instance.Dispose();
        _vibrance.Dispose();
        GammaRamp.Reset(_monitorDC);
        GammaRamp.FreeDC(_monitorDC);
        SaveSettings();
    }

    // ── Window chrome / title bar theming ─────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>Apply title bar theme + resource refs. Win11 provides rounded corners natively.</summary>
    public static void ThemeWindow(Window w, string mode = "Dark")
    {
        ThemeTitleBar(w, mode);
        w.SetResourceReference(Window.BackgroundProperty, "Brush.Canvas");
        w.SetResourceReference(Window.ForegroundProperty, "Brush.TextPrimary");
    }

    /// <summary>Re-apply DWM dark/light attribute — call after theme switch.</summary>
    public static void ThemeTitleBar(Window w, string mode)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();
        var useDark = mode != "Light" ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, 4);
    }

    // ── Theme (Dark / Light / System) ──────────────────

    /// <summary>Update token brush colors in-place — keeps DynamicResource bindings alive.</summary>
    public static void ApplyTheme(string mode, Window? w = null)
    {
        if (mode == "System")
            mode = SystemThemeIsDark() ? "Dark" : "Light";

        // Find the current token dictionary (always loaded, never removed)
        var dict = Application.Current.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.Contains("Tokens.") == true);
        if (dict == null) return;

        // Load target token file to read new colors
        var next = new ResourceDictionary
        {
            Source = new Uri($"/Themes/Tokens.{mode}.xaml", UriKind.Relative)
        };

        // Update colors in-place — DynamicResource references survive because keys and brush instances stay
        foreach (var key in next.Keys)
        {
            if (dict[key] is SolidColorBrush oldBrush && next[key] is SolidColorBrush newBrush)
            {
                if (oldBrush.IsFrozen)
                    dict[key] = new SolidColorBrush(newBrush.Color);
                else
                    oldBrush.Color = newBrush.Color;
            }
        }

        // Re-theme the persistent window's title bar
        if (w != null)
            ThemeTitleBar(w, mode);
    }

    private static bool SystemThemeIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 0;
        }
        catch { }
        return false; // default to light if unreadable
    }

    // ── Gamma application ──────────────────────────────

    private void ApplyDefault()
    {
        _vibrance.CurrentLevel = _settings.DefaultVibrance;
        GammaRamp.Apply(_monitorDC, _settings.DefaultBrightness,
            _settings.DefaultContrast, _settings.DefaultGamma);
        _isDefaultActive = true;
        _activeProfile = null;
    }

    private void ApplyProfile(AppProfile profile)
    {
        _vibrance.CurrentLevel = profile.Vibrance;
        GammaRamp.Apply(_monitorDC, profile.Brightness, profile.Contrast, profile.Gamma);
        _isDefaultActive = false;
        _activeProfile = profile;
    }

    private void OnForegroundChanged(object? sender, WinEventHookEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var profile = _settings.Profiles.FirstOrDefault(p =>
                p.ProcessName.Equals(e.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (profile != null)
            {
                if (profile != _activeProfile)
                    ApplyProfile(profile);
            }
            else if (!_isDefaultActive)
            {
                ApplyDefault();
            }
        });
    }

    // ── Default slider changes ─────────────────────────

    private void DefaultSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;

        _settings.DefaultVibrance = (int)DefaultVibrance.Value;
        _settings.DefaultBrightness = DefaultBrightness.Value;
        _settings.DefaultContrast = DefaultContrast.Value;
        _settings.DefaultGamma = DefaultGamma.Value;

        UpdateTextBoxes();

        if (_isDefaultActive)
            ApplyDefault();
    }

    private static string Pct(double v) => $"{v * 100:F0}%";
    private static string Vib(int v) => $"{v}%";
    private static string Gam(double v) => $"{v:F2}";

    private void UpdateTextBoxes()
    {
        DefaultVibranceText.Text = Vib((int)DefaultVibrance.Value);
        DefaultBrightnessText.Text = Pct(DefaultBrightness.Value);
        DefaultContrastText.Text = Pct(DefaultContrast.Value);
        DefaultGammaText.Text = Gam(DefaultGamma.Value);
    }

    private void ResetDefault_Click(object sender, RoutedEventArgs e)
    {
        _settings.DefaultVibrance = 50;
        _settings.DefaultBrightness = 0.50;
        _settings.DefaultContrast = 0.50;
        _settings.DefaultGamma = 1.0;
        DefaultVibrance.Value = 50;
        DefaultBrightness.Value = 0.50;
        DefaultContrast.Value = 0.50;
        DefaultGamma.Value = 1.0;
        UpdateTextBoxes();
        if (_isDefaultActive) ApplyDefault();
    }

    private void TextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var tb = (TextBox)sender;
        var s = tb.Text.TrimEnd('%');
        if (tb == DefaultVibranceText)
        {
            if (int.TryParse(s, out int vi))
                DefaultVibrance.Value = Math.Clamp(vi, 0, 100);
        }
        else if (double.TryParse(s, out double val))
        {
            if (tb == DefaultBrightnessText) DefaultBrightness.Value = Math.Clamp(val / 100.0, 0, 1);
            else if (tb == DefaultContrastText) DefaultContrast.Value = Math.Clamp(val / 100.0, 0, 1);
            else if (tb == DefaultGammaText) DefaultGamma.Value = Math.Clamp(val, 0.5, 3.5);
        }
        UpdateTextBoxes();
    }

    // ── Card grid ──────────────────────────────────────

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = _settings.Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProfileCountBadge.Text = _settings.Profiles.Count.ToString();
    }

    private void RefreshCardGrid()
    {
        ProfileCards.ItemsSource = null;
        ProfileCards.ItemsSource = _settings.Profiles;
    }

    private static AppProfile? ProfileFromSender(object sender)
    {
        if (sender is Button btn) return btn.Tag as AppProfile;
        if (sender is FrameworkElement fe) return fe.Tag as AppProfile;
        return null;
    }

    private void Card_Edit(object sender, MouseButtonEventArgs e)
    {
        if (ProfileFromSender(sender) is AppProfile profile)
            EditProfile(profile);
    }

    private void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ProfileFromSender(sender) is not AppProfile profile) return;

        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Edit" };
        edit.Click += (_, _) => EditProfile(profile);
        var remove = new MenuItem { Header = "Remove" };
        remove.Click += (_, _) => RemoveProfile(profile);
        menu.Items.Add(edit);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);

        if (sender is Border b)
        {
            menu.PlacementTarget = b;
            menu.IsOpen = true;
        }
    }

    private void Card_EditBtn(object sender, RoutedEventArgs e)
    {
        if (ProfileFromSender(sender) is AppProfile profile)
            EditProfile(profile);
    }

    private void Card_RemoveBtn(object sender, RoutedEventArgs e)
    {
        if (ProfileFromSender(sender) is AppProfile profile)
            RemoveProfile(profile);
    }

    private void EditProfile(AppProfile profile)
    {
        var editor = new ProfileEditorWindow(profile) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            RefreshCardGrid();
            if (profile == _activeProfile)
                ApplyProfile(profile);
            SaveSettings();
        }
    }

    private void RemoveProfile(AppProfile profile)
    {
        _settings.Profiles.Remove(profile);
        RefreshCardGrid();
        UpdateEmptyState();
        SaveSettings();
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        // ponytail: if owner is hidden (tray), restore it, then defer dialog so WPF settles
        if (Visibility != Visibility.Visible)
        {
            ShowWindow();
            Dispatcher.BeginInvoke(() => AddApp_Click(sender, e));
            return;
        }

        var picker = new ProcessPickerWindow { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedProcess != null)
        {
            var profile = new AppProfile
            {
                ProcessName = picker.SelectedProcess.ProcessName,
                FilePath = picker.SelectedProcess.FilePath
            };
            profile.LoadIcon(picker.SelectedProcess.FilePath);
            _settings.Profiles.Add(profile);
            RefreshCardGrid();
            UpdateEmptyState();
            SaveSettings();
        }
    }

    // ── Settings popup ─────────────────────────────────

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var popup = new SettingsWindow(_settings) { Owner = this };
        if (popup.ShowDialog() == true)
        {
            ApplyTheme(_settings.ThemeMode, this);
            SetAutoStart(_settings.AutoStart);
            SaveSettings();
        }
    }

    // ── Auto-start ─────────────────────────────────────

    private void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, writable: true);
            if (key == null) return;
            if (enable)
            {
                var exePath = Environment.ProcessPath ?? "";
                key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    // ── Persistence ────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            var localConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var path = File.Exists(_settingsPath) ? _settingsPath :
                       File.Exists(localConfig) ? localConfig : null;
            if (path != null)
            {
                var json = File.ReadAllText(path);
                _settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new();
            }
        }
        catch
        {
            _settings = new AppSettings();
        }
        SetAutoStart(_settings.AutoStart);
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_settings, JsonOpts);
            File.WriteAllText(_settingsPath, json);
        }
        catch { /* best effort */ }
    }
}

// ── Settings Window ────────────────────────────────────────

public class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly RadioButton _darkRb, _lightRb, _systemRb;
    private readonly CheckBox _autoStartCb;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        Title = "Settings";
        Width = 320; Height = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var themeLabel = new TextBlock
        {
            Text = "Theme",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 8, 0, 8)
        };
        themeLabel.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");

        _darkRb = new RadioButton { Content = "Dark", Margin = new Thickness(8, 0, 0, 4), FontSize = 13 };
        _darkRb.SetResourceReference(RadioButton.ForegroundProperty, "Brush.TextPrimary");
        _lightRb = new RadioButton { Content = "Light", Margin = new Thickness(8, 0, 0, 4), FontSize = 13 };
        _lightRb.SetResourceReference(RadioButton.ForegroundProperty, "Brush.TextPrimary");
        _systemRb = new RadioButton { Content = "System", Margin = new Thickness(8, 0, 0, 4), FontSize = 13 };
        _systemRb.SetResourceReference(RadioButton.ForegroundProperty, "Brush.TextPrimary");

        switch (settings.ThemeMode)
        {
            case "Light": _lightRb.IsChecked = true; break;
            case "System": _systemRb.IsChecked = true; break;
            default: _darkRb.IsChecked = true; break;
        }

        _autoStartCb = new CheckBox
        {
            Content = "Start with Windows",
            IsChecked = settings.AutoStart,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 13
        };
        _autoStartCb.SetResourceReference(CheckBox.ForegroundProperty, "Brush.TextPrimary");

        var stack = new StackPanel();
        stack.Children.Add(themeLabel);
        stack.Children.Add(_darkRb);
        stack.Children.Add(_lightRb);
        stack.Children.Add(_systemRb);
        stack.Children.Add(new Border { Height = 12 });
        stack.Children.Add(_autoStartCb);
        Grid.SetRow(stack, 0);

        var buttons = new DockPanel { Margin = new Thickness(0, 20, 0, 0) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        ok.Click += (_, _) =>
        {
            settings.ThemeMode = _darkRb.IsChecked == true ? "Dark"
                               : _lightRb.IsChecked == true ? "Light"
                               : "System";
            settings.AutoStart = _autoStartCb.IsChecked == true;
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };
        cancel.SetResourceReference(StyleProperty, "PrimaryButton");
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        Grid.SetRow(buttons, 2);

        grid.Children.Add(stack);
        grid.Children.Add(buttons);
        Content = grid;

        MainWindow.ThemeWindow(this);
    }
}

// ── Process Picker Window ──────────────────────────────────

public class ProcessPickerWindow : Window
{
    public ProcessInfo? SelectedProcess { get; private set; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags,
        StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Reliable cross-bitness process path. Falls back to MainModule, then P/Invoke.</summary>
    public static string GetProcessPath(Process p)
    {
        try { return p.MainModule?.FileName ?? ""; }
        catch { }

        try
        {
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
            if (h == IntPtr.Zero) return "";
            var sb = new StringBuilder(512);
            int size = 512;
            var ok = QueryFullProcessImageName(h, 0, sb, ref size);
            CloseHandle(h);
            return ok ? sb.ToString() : "";
        }
        catch { return ""; }
    }

    public ProcessPickerWindow()
    {
        Title = "Select Application";
        Width = 450; Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;

        var grid = new Grid { Margin = new Thickness(10) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());

        var filterTb = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        filterTb.SetResourceReference(StyleProperty, typeof(TextBox));
        Grid.SetRow(filterTb, 0);

        var hint = new TextBlock
        {
            Text = "Type to filter, double-click to select",
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(2, 0, 0, 6)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        Grid.SetRow(hint, 1);

        var lv = new ListView();
        lv.Background = (Brush)Application.Current.FindResource("Brush.SurfaceAlt");
        lv.Foreground = (Brush)Application.Current.FindResource("Brush.TextPrimary");
        lv.BorderBrush = (Brush)Application.Current.FindResource("Brush.Border");
        lv.MouseDoubleClick += (_, _) =>
        {
            if (lv.SelectedItem is ProcessInfo pi) { SelectedProcess = pi; DialogResult = true; }
        };
        lv.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && lv.SelectedItem is ProcessInfo pi) { SelectedProcess = pi; DialogResult = true; }
        };

        // Override default WPF hover (bright blue) with theme-appropriate color
        var hoverBrush = (Brush)Application.Current.FindResource("Brush.AccentSubtle");
        var selectedBrush = (Brush)Application.Current.FindResource("Brush.AccentPressed");
        var lviStyle = new Style(typeof(ListViewItem));
        lviStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        lviStyle.Setters.Add(new Setter(ForegroundProperty, lv.Foreground));
        lviStyle.Setters.Add(new Setter(FontSizeProperty, 13.0));
        var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(BackgroundProperty, hoverBrush));
        lviStyle.Triggers.Add(hoverTrigger);
        var selectedTrigger = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(BackgroundProperty, selectedBrush));
        lviStyle.Triggers.Add(selectedTrigger);
        lv.ItemContainerStyle = lviStyle;

        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Process", Width = 140, DisplayMemberBinding = new Binding("ProcessName") });
        view.Columns.Add(new GridViewColumn { Header = "PID", Width = 60, DisplayMemberBinding = new Binding("Pid") });
        view.Columns.Add(new GridViewColumn { Header = "Title", Width = 190, DisplayMemberBinding = new Binding("WindowTitle") });
        lv.View = view;

        var processes = Process.GetProcesses()
            .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle) && p.MainWindowHandle != IntPtr.Zero)
            .Select(p =>
            {
                return new ProcessInfo
                {
                    ProcessName = p.ProcessName,
                    Pid = p.Id,
                    WindowTitle = p.MainWindowTitle,
                    FilePath = GetProcessPath(p)
                };
            })
            .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lv.ItemsSource = processes;
        Grid.SetRow(lv, 2);

        filterTb.TextChanged += (_, _) => FilterList(filterTb.Text);

        grid.Children.Add(filterTb);
        grid.Children.Add(hint);
        grid.Children.Add(lv);
        Content = grid;

        MainWindow.ThemeWindow(this);
        filterTb.Focus();

        void FilterList(string text)
        {
            lv.ItemsSource = string.IsNullOrWhiteSpace(text) ? processes
                : processes.Where(p =>
                    p.ProcessName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    p.WindowTitle.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }
}

public class ProcessInfo
{
    public string ProcessName { get; init; } = "";
    public int Pid { get; init; }
    public string WindowTitle { get; init; } = "";
    public string FilePath { get; init; } = "";
}

// ── Profile Editor Window ──────────────────────────────────

public class ProfileEditorWindow : Window
{
    private readonly AppProfile _profile;
    private readonly Slider _vibSlider, _brightSlider, _contrastSlider, _gammaSlider;
    private readonly TextBox _vibTb, _brightTb, _contrastTb, _gammaTb;

    public ProfileEditorWindow(AppProfile profile)
    {
        _profile = profile;
        Title = $"Edit: {profile.ProcessName}";
        Width = 470; Height = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var panel = new StackPanel();

        _vibSlider = NewIntSlider(profile.Vibrance, 0, 100);
        _brightSlider = NewSlider(profile.Brightness);
        _contrastSlider = NewSlider(profile.Contrast);
        _gammaSlider = NewSlider(profile.Gamma, 0.5, 3.5);

        _vibTb = NewValueField();
        _brightTb = NewValueField();
        _contrastTb = NewValueField();
        _gammaTb = NewValueField();

        panel.Children.Add(MakeRow("Vibrance", _vibSlider, _vibTb));
        panel.Children.Add(MakeRow("Brightness", _brightSlider, _brightTb));
        panel.Children.Add(MakeRow("Contrast", _contrastSlider, _contrastTb));
        panel.Children.Add(MakeRow("Gamma", _gammaSlider, _gammaTb));

        UpdateTexts();

        _vibSlider.ValueChanged += (_, _) => UpdateTexts();
        _brightSlider.ValueChanged += (_, _) => UpdateTexts();
        _contrastSlider.ValueChanged += (_, _) => UpdateTexts();
        _gammaSlider.ValueChanged += (_, _) => UpdateTexts();

        void SyncTbToSlider(TextBox tb)
        {
            var s = tb.Text.TrimEnd('%');
            if (tb == _vibTb)
            {
                if (int.TryParse(s, out int vi))
                    _vibSlider.Value = Math.Clamp(vi, (int)_vibSlider.Minimum, (int)_vibSlider.Maximum);
            }
            else if (double.TryParse(s, out double val))
            {
                if (tb == _brightTb) _brightSlider.Value = Math.Clamp(val / 100.0, 0, 1);
                else if (tb == _contrastTb) _contrastSlider.Value = Math.Clamp(val / 100.0, 0, 1);
                else if (tb == _gammaTb) _gammaSlider.Value = Math.Clamp(val, 0.5, 3.5);
            }
            UpdateTexts();
        }

        _vibTb.LostFocus += (_, _) => SyncTbToSlider(_vibTb);
        _brightTb.LostFocus += (_, _) => SyncTbToSlider(_brightTb);
        _contrastTb.LostFocus += (_, _) => SyncTbToSlider(_contrastTb);
        _gammaTb.LostFocus += (_, _) => SyncTbToSlider(_gammaTb);
        _vibTb.KeyDown += (_, e) => { if (e.Key == Key.Enter) SyncTbToSlider(_vibTb); };
        _brightTb.KeyDown += (_, e) => { if (e.Key == Key.Enter) SyncTbToSlider(_brightTb); };
        _contrastTb.KeyDown += (_, e) => { if (e.Key == Key.Enter) SyncTbToSlider(_contrastTb); };
        _gammaTb.KeyDown += (_, e) => { if (e.Key == Key.Enter) SyncTbToSlider(_gammaTb); };

        Grid.SetRow(panel, 0);

        var saveBtn = new Button { Content = "Save", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        saveBtn.SetResourceReference(StyleProperty, "PrimaryButton");
        saveBtn.Click += (_, _) =>
        {
            _profile.Vibrance = (int)_vibSlider.Value;
            _profile.Brightness = _brightSlider.Value;
            _profile.Contrast = _contrastSlider.Value;
            _profile.Gamma = _gammaSlider.Value;
            DialogResult = true;
        };
        Grid.SetRow(saveBtn, 1);

        grid.Children.Add(panel);
        grid.Children.Add(saveBtn);
        Content = grid;

        MainWindow.ThemeWindow(this);

        void UpdateTexts()
        {
            _vibTb.Text = $"{(int)_vibSlider.Value}%";
            _brightTb.Text = $"{_brightSlider.Value * 100:F0}%";
            _contrastTb.Text = $"{_contrastSlider.Value * 100:F0}%";
            _gammaTb.Text = $"{_gammaSlider.Value:F2}";
        }
    }

    private static Slider NewSlider(double value, double min = 0, double max = 1)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value,
                            TickFrequency = 0.01, IsSnapToTickEnabled = true, Width = 200 };
        s.SetResourceReference(StyleProperty, "ModernSlider");
        return s;
    }

    private static Slider NewIntSlider(int value, int min, int max)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value,
                            TickFrequency = 1, IsSnapToTickEnabled = true, Width = 200 };
        s.SetResourceReference(StyleProperty, "ModernSlider");
        return s;
    }

    private static TextBox NewValueField()
    {
        // No explicit width — ValueField style sets MinWidth=64
        var tb = new TextBox { TextAlignment = TextAlignment.Center };
        tb.SetResourceReference(StyleProperty, "ValueField");
        return tb;
    }

    private static Grid MakeRow(string label, Slider slider, TextBox tb)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(l, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(tb, 3);
        g.Children.Add(l);
        g.Children.Add(slider);
        g.Children.Add(tb);
        return g;
    }
}
