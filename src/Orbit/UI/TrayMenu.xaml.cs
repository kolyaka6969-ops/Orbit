using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Orbit.Shell;

namespace Orbit.UI;

/// <summary>Меню значка в трее в стиле Windows 11 (стандартное меню WinForms выглядит как в Windows 7).</summary>
public partial class TrayMenu : Window
{
    private readonly App _app;
    private bool _closing;

    internal TrayMenu(App app)
    {
        InitializeComponent();
        _app = app;
        var paused = app.Settings.Paused;
        Status.Text = paused ? "Приостановлен" : "Работает в фоне";
        PauseGlyph.Text = paused ? "" : "";
        PauseText.Text = paused ? "Возобновить" : "Приостановить";
        SourceInitialized += (_, _) => Native.AddExStyle(new WindowInteropHelper(this).Handle, Native.WS_EX_TOOLWINDOW);
        Deactivated += (_, _) => CloseMenu();
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
                CloseMenu();
        };
    }

    public void ShowAtCursor()
    {
        Opacity = 0;
        Left = -32000;
        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.PlaceWindow(hwnd, Native.CursorPosition(), centered: false);
        Native.ForceForeground(hwnd);
        Activate();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void CloseMenu()
    {
        if (_closing)
            return;
        _closing = true;
        Close();
    }

    private void Run(Action action)
    {
        CloseMenu();
        Dispatcher.BeginInvoke(action);
    }

    private void OnClipboard(object sender, RoutedEventArgs e) => Run(_app.OpenForClipboard);

    private void OnPause(object sender, RoutedEventArgs e) => Run(() => _app.SetPaused(!_app.Settings.Paused));

    private void OnSettings(object sender, RoutedEventArgs e) => Run(_app.ShowSettings);

    private void OnExit(object sender, RoutedEventArgs e) => Run(_app.Quit);
}
