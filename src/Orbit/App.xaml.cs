using System.Windows;
using System.Windows.Threading;
using Orbit.Core;
using Orbit.Shell;
using Orbit.UI;

namespace Orbit;

public partial class App : Application
{
    private SingleInstance? _instance;
    private GlobalInput? _input;
    private HotkeyListener? _hotkey;
    private TrayIcon? _tray;
    private WheelController? _wheels;
    private JobRunner? _jobs;
    private JobsFlyout? _flyout;
    private SettingsWindow? _settingsWindow;
    private TrayMenu? _trayMenu;

    internal Settings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (args.Length > 0 && args[0] is "--run" or "--list" or "--probe" or "--render" or "--video-edit" or "--frame" or "--join")
        {
            Cli.Run(this, args);
            return;
        }
        if (args.Contains("--exit"))
        {
            // Установщик перед обновлением закрывает запущенную копию.
            SingleInstance.Forward(["--exit"]);
            Shutdown();
            return;
        }
        if (args.Contains("--unregister"))
        {
            // Вызывается деинсталлятором: убираем следы в реестре и закрываем запущенную копию.
            try
            {
                Autostart.Set(false);
                ExplorerIntegration.SetContextMenu(false);
            }
            catch (Exception)
            {
            }
            SingleInstance.Forward(["--exit"]);
            Shutdown();
            return;
        }

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            SingleInstance.Forward(args);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        Settings = Settings.Load();
        ThemeManager.Initialize(Settings.Theme);

        // Программу могли переустановить в другую папку — обновляем пути в реестре.
        Autostart.RefreshPath();
        ExplorerIntegration.RefreshPath();

        _jobs = new JobRunner(Settings, Dispatcher);
        // Панель создаётся при первом показе: в фоне (автозапуск) она может так и не понадобиться.
        _jobs.Started += job =>
        {
            if (job.Operation is { Interactive: true })
                return;
            if (Settings.ShowNotifications || job.State == JobState.Info)
                (_flyout ??= new JobsFlyout(_jobs)).ShowPanel();
        };
        _jobs.Finished += job =>
        {
            if (job.Operation is { Interactive: true } && job.State == JobState.Canceled)
                return;
            if (Settings.ShowNotifications || job.State == JobState.Failed)
                (_flyout ??= new JobsFlyout(_jobs)).ShowPanel();
            else
                _flyout?.ScheduleHide();
        };

        _input = new GlobalInput(Dispatcher);
        try
        {
            _input.Start();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Orbit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _wheels = new WheelController(Settings, _jobs, _input);
        _wheels.Notification += (title, text) => _jobs.Notify(title, text);

        _hotkey = new HotkeyListener();
        _hotkey.Pressed += () => _wheels.OnHotkey();
        ApplyHotkey();

        _tray = new TrayIcon();
        _tray.Clicked += ShowSettings;
        _tray.MenuRequested += ShowTrayMenu;
        _tray.SetPaused(Settings.Paused);

        _instance.Listen(Dispatcher, OnArgs);

        var autostart = args.Contains(Autostart.Argument);
        var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (files.Count > 0)
            _wheels.QueuePaths(files);
        else if (!autostart)
            ShowSettings();

        if (!Settings.Welcomed)
        {
            Settings.Welcomed = true;
            Settings.Save();
            if (autostart)
                _jobs.Notify("Orbit работает", "Перетащите файл и нажмите Shift, Ctrl или Alt.");
        }
    }

    /// <summary>Аргументы от повторного запуска (меню «Пуск», контекстное меню Проводника, деинсталлятор).</summary>
    private void OnArgs(string[] args)
    {
        if (args.Contains("--exit"))
        {
            Quit();
            return;
        }
        var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (files.Count > 0)
            _wheels?.QueuePaths(files);
        else if (!args.Contains(Autostart.Argument))
            ShowSettings();
    }

    public void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(_settingsWindow).Handle);
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ShowTrayMenu()
    {
        _trayMenu?.Close();
        _trayMenu = new TrayMenu(this);
        _trayMenu.Closed += (_, _) => _trayMenu = null;
        _trayMenu.ShowAtCursor();
    }

    public void OpenForClipboard() => _wheels?.OpenForClipboard();

    public void SetPaused(bool paused)
    {
        Settings.Paused = paused;
        Settings.Save();
        _tray?.SetPaused(paused);
        ApplyHotkey();
    }

    /// <summary>Включает горячую клавишу согласно настройкам; false — если её заняла другая программа.</summary>
    public bool ApplyHotkey() => _hotkey?.SetEnabled(Settings.HotkeyEnabled && !Settings.Paused) ?? true;

    public void Quit()
    {
        foreach (var job in _jobs?.Jobs.Where(j => j.IsRunning).ToList() ?? [])
            job.Cancellation.Cancel();
        _tray?.Dispose();
        _hotkey?.Dispose();
        _input?.Dispose();
        _instance?.Dispose();
        Shutdown();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Программа живёт в фоне — одна ошибка не должна её ронять.
        e.Handled = true;
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path.Combine(Settings.Folder, "errors.log"), $"{DateTime.Now:O}\n{e.Exception}\n\n");
        }
        catch (IOException)
        {
        }
        _jobs?.Notify("Что-то пошло не так", e.Exception.Message);
    }
}
