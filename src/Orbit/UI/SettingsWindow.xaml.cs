using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Orbit.Core;
using Orbit.Shell;

namespace Orbit.UI;

/// <summary>Настройки в стиле «Параметров» Windows 11: Mica, навигация слева, карточки справа.</summary>
public partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly Settings _settings;
    private bool _loading = true;

    internal SettingsWindow(App app)
    {
        _app = app;
        _settings = app.Settings;
        ThemeManager.UseMica(this);
        InitializeComponent();

        var version = typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0);
        // Отдельная сборка для Windows 10 без обновлений помечена в Orbit.csproj.
        var edition = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "Edition")?.Value;
        VersionText.Text = $"Версия {version.Major}.{version.Minor}.{version.Build}" + (edition is null ? "" : $" для {edition}");
        AboutTitle.Text = $"Orbit {version.Major}.{version.Minor}";
        FormatList.ItemsSource = FormatRows();
        Load();
        _settings.Changed += OnSettingsChanged;
        Closed += (_, _) => _settings.Changed -= OnSettingsChanged;
        Activated += (_, _) => RefreshSystemState();
        _loading = false;
    }

    private void Load()
    {
        _loading = true;
        RefreshSystemState();
        PauseToggle.IsChecked = _settings.Paused;
        PausedBanner.Visibility = _settings.Paused ? Visibility.Visible : Visibility.Collapsed;
        NotifyToggle.IsChecked = _settings.ShowNotifications;
        ThemeBox.SelectedIndex = (int)_settings.Theme;
        ShiftToggle.IsChecked = _settings.ShiftWheel;
        CtrlToggle.IsChecked = _settings.CtrlWheel;
        AltToggle.IsChecked = _settings.AltWheel;
        HotkeyToggle.IsChecked = _settings.HotkeyEnabled;
        OutputBox.SelectedIndex = (int)_settings.Output;
        UpdateCustomFolder();
        QualitySlider.Value = _settings.ImageQuality;
        QualityText.Text = _settings.ImageQuality.ToString();
        VideoBox.SelectedIndex = (int)_settings.Video;
        DpiBox.SelectedIndex = _settings.PdfDpi switch { <= 96 => 0, >= 300 => 2, _ => 1 };
        _loading = false;
    }

    /// <summary>Автозапуск и контекстное меню живут в реестре — их могли поменять снаружи (например, в «Диспетчере задач»).</summary>
    private void RefreshSystemState()
    {
        var wasLoading = _loading;
        _loading = true;
        AutostartToggle.IsChecked = Autostart.IsEnabled;
        ContextMenuToggle.IsChecked = ExplorerIntegration.IsContextMenuEnabled;
        _loading = wasLoading;
    }

    private void OnSettingsChanged()
    {
        if (!_loading)
            Dispatcher.BeginInvoke(Load);
    }

    private void Save()
    {
        if (!_loading)
            _settings.Save();
    }

    // ------------------------------------------------------------ навигация

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (PageGeneral is null)
            return;
        PageGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageGestures.Visibility = NavGestures.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageOutput.Visibility = NavOutput.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageFormats.Visibility = NavFormats.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ScrollToTop();
    }

    // ------------------------------------------------------------ главная

    private void OnAutostart(object sender, RoutedEventArgs e)
    {
        try
        {
            Autostart.Set(AutostartToggle.IsChecked == true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(this, "Не удалось изменить автозапуск: нет доступа к реестру.", "Orbit");
        }
        RefreshSystemState();
    }

    private void OnPauseToggle(object sender, RoutedEventArgs e) => _app.SetPaused(PauseToggle.IsChecked == true);

    private void OnResume(object sender, RoutedEventArgs e) => _app.SetPaused(false);

    private void OnNotify(object sender, RoutedEventArgs e)
    {
        _settings.ShowNotifications = NotifyToggle.IsChecked == true;
        Save();
    }

    private void OnTheme(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedIndex < 0)
            return;
        _settings.Theme = (ThemeChoice)ThemeBox.SelectedIndex;
        Save();
        ThemeManager.Apply(_settings.Theme);
    }

    // ------------------------------------------------------------ жесты

    private void OnWheelToggle(object sender, RoutedEventArgs e)
    {
        _settings.ShiftWheel = ShiftToggle.IsChecked == true;
        _settings.CtrlWheel = CtrlToggle.IsChecked == true;
        _settings.AltWheel = AltToggle.IsChecked == true;
        Save();
    }

    private void OnHotkey(object sender, RoutedEventArgs e)
    {
        _settings.HotkeyEnabled = HotkeyToggle.IsChecked == true;
        Save();
        if (!_app.ApplyHotkey())
        {
            MessageBox.Show(this, "Сочетание Ctrl + Alt + Пробел уже занято другой программой.", "Orbit", MessageBoxButton.OK, MessageBoxImage.Information);
            HotkeyToggle.IsChecked = false;
            _settings.HotkeyEnabled = false;
            Save();
        }
    }

    private void OnContextMenu(object sender, RoutedEventArgs e)
    {
        ExplorerIntegration.SetContextMenu(ContextMenuToggle.IsChecked == true);
        RefreshSystemState();
    }

    // ------------------------------------------------------------ сохранение

    private void OnOutput(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OutputBox.SelectedIndex < 0)
            return;
        var mode = (OutputMode)OutputBox.SelectedIndex;
        if (mode == OutputMode.Custom && string.IsNullOrWhiteSpace(_settings.CustomFolder) && !PickFolder())
        {
            OutputBox.SelectedIndex = (int)_settings.Output;
            return;
        }
        _settings.Output = mode;
        Save();
        UpdateCustomFolder();
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        if (PickFolder())
        {
            Save();
            UpdateCustomFolder();
        }
    }

    private bool PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Куда сохранять результаты?" };
        if (dialog.ShowDialog(this) != true)
            return false;
        _settings.CustomFolder = dialog.FolderName;
        return true;
    }

    private void UpdateCustomFolder()
    {
        CustomFolderCard.Visibility = _settings.Output == OutputMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        CustomFolderCard.Description = _settings.CustomFolder ?? "Папка не выбрана";
    }

    private void OnQuality(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityText is null)
            return;
        QualityText.Text = ((int)QualitySlider.Value).ToString();
        if (_loading)
            return;
        _settings.ImageQuality = (int)QualitySlider.Value;
        Save();
    }

    private void OnVideo(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || VideoBox.SelectedIndex < 0)
            return;
        _settings.Video = (VideoQuality)VideoBox.SelectedIndex;
        Save();
    }

    private void OnDpi(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DpiBox.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        _settings.PdfDpi = int.Parse(tag);
        Save();
    }

    // ------------------------------------------------------------ о программе

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Settings.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.Folder}\"") { UseShellExecute = true });
    }

    private void OnExit(object sender, RoutedEventArgs e) => _app.Quit();

    // ------------------------------------------------------------ таблица форматов

    public sealed record FormatRow(string Glyph, string Title, string Inputs, string Outputs);

    private static IReadOnlyList<FormatRow> FormatRows() =>
    [
        new(Glyphs.Photo, "Изображения", Join(Formats.Images), "→ PNG, JPG, WEBP, AVIF, TIFF, GIF, ICO, PDF · сжатие, размер, поворот, текст (OCR)"),
        new("", "RAW с фотокамер", Join(Formats.Raw), "→ любые форматы изображений"),
        new(Glyphs.Audio, "Аудио", Join(Formats.Audio), "→ MP3, M4A, WAV, FLAC, WMA"),
        new(Glyphs.Video, "Видео", Join(Formats.Video), "→ MP4 (H.264), WMV, MP3, M4A · сжатие, 720p, без звука"),
        new(Glyphs.Pdf, "PDF", "pdf", "→ PNG, JPG по страницам · объединить, разделить"),
        new(Glyphs.Zip, "Архивы", Join(Formats.Archives), "→ распаковать в папку · любые файлы и папки → ZIP"),
    ];

    private static string Join(IEnumerable<string> extensions) => string.Join(", ", extensions);
}
