using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Orbit.Core;
using Orbit.Shell;
using Windows.UI.ViewManagement;

namespace Orbit.UI;

/// <summary>
/// Светлая/тёмная тема и системный акцентный цвет. Следит за сменой темы Windows и перекрашивает всё на лету.
/// </summary>
internal static class ThemeManager
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2; // Mica

    private static ThemeChoice _choice;
    private static readonly UISettings UiSettings = new();

    public static bool IsDark { get; private set; }

    /// <summary>Mica официально доступна с Windows 11 22H2.</summary>
    public static bool MicaSupported { get; } = Environment.OSVersion.Version.Build >= 22621;

    public static bool IsWindows11 { get; } = Environment.OSVersion.Version.Build >= 22000;

    public static event Action? Changed;

    public static void Initialize(ThemeChoice choice)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current.Dispatcher.BeginInvoke(() => Apply(_choice));
        };
        UiSettings.ColorValuesChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => Apply(_choice));
        Apply(choice);
    }

    public static void Apply(ThemeChoice choice)
    {
        _choice = choice;
        IsDark = choice switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => SystemUsesDarkTheme(),
        };

        var resources = Application.Current.Resources;
        resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Orbit;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml"),
        };
        ApplyAccent(resources);

        foreach (Window window in Application.Current.Windows)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
                SetDarkFrame(hwnd, IsDark);
        }
        Changed?.Invoke();
    }

    private static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static void ApplyAccent(ResourceDictionary resources)
    {
        Color accent, text;
        try
        {
            // Как в WinUI: в светлой теме — тёмный оттенок акцента, в тёмной — светлый.
            var value = UiSettings.GetColorValue(IsDark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
            accent = Color.FromRgb(value.R, value.G, value.B);
            var textValue = UiSettings.GetColorValue(IsDark ? UIColorType.AccentLight3 : UIColorType.AccentDark2);
            text = Color.FromRgb(textValue.R, textValue.G, textValue.B);
        }
        catch (Exception)
        {
            accent = IsDark ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8);
            text = IsDark ? Color.FromRgb(0x99, 0xEB, 0xFF) : Color.FromRgb(0x00, 0x3E, 0x92);
        }
        resources["AccentColor"] = accent;
        resources["AccentBrush"] = Frozen(accent);
        resources["AccentHoverBrush"] = Frozen(Color.FromArgb(0xE6, accent.R, accent.G, accent.B));
        resources["AccentPressedBrush"] = Frozen(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
        resources["AccentSubtleBrush"] = Frozen(Color.FromArgb(0x26, accent.R, accent.G, accent.B));
        resources["AccentTextBrush"] = Frozen(text);
        resources["TextOnAccent"] = Frozen(IsDark ? Colors.Black : Colors.White);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static void SetDarkFrame(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    /// <summary>
    /// Окно с фоном Mica, как у «Параметров» Windows 11: рамка DWM растягивается на всё окно, а WPF рисует
    /// прозрачный фон. На Windows 10 — обычный сплошной фон темы.
    /// </summary>
    public static void UseMica(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            SetDarkFrame(hwnd, IsDark);
            if (!MicaSupported)
            {
                window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
                return;
            }
            window.Background = Brushes.Transparent;
            HwndSource.FromHwnd(hwnd)!.CompositionTarget.BackgroundColor = Colors.Transparent;
            var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            var backdrop = DWMSBT_MAINWINDOW;
            Native.DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        };
    }
}
