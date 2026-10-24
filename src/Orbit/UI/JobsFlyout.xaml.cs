using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Orbit.Shell;

namespace Orbit.UI;

/// <summary>
/// Панель задач в правом нижнем углу, как уведомления Windows 11: прогресс, итог, кнопка «Показать в папке».
/// Не забирает фокус у окна, в котором работает пользователь.
/// </summary>
public partial class JobsFlyout : Window
{
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly System.Collections.ObjectModel.ObservableCollection<Job> _jobs;
    private bool _shown;

    internal JobsFlyout(JobRunner runner)
    {
        InitializeComponent();
        _jobs = runner.Jobs;
        List.ItemsSource = runner.Jobs;
        _hideTimer.Tick += (_, _) =>
        {
            if (IsMouseOver)
                return;
            _hideTimer.Stop();
            HidePanel();
        };
        SourceInitialized += (_, _) =>
            Native.AddExStyle(new WindowInteropHelper(this).Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        SizeChanged += (_, _) => Place();
        MouseEnter += (_, _) => _hideTimer.Stop();
        MouseLeave += (_, _) => ScheduleHide();
    }

    /// <summary>Показывает панель (если скрыта) и прячет её через 5 секунд, когда всё готово без ошибок.</summary>
    public void ShowPanel()
    {
        _hideTimer.Stop();
        if (!_shown)
        {
            _shown = true;
            Opacity = 0;
            Show();
            Place();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        ScheduleHide();
    }

    public void ScheduleHide()
    {
        _hideTimer.Stop();
        if (_jobs.Any(j => j.IsRunning || j.State == JobState.Failed))
            return;
        _hideTimer.Start();
    }

    private void HidePanel()
    {
        if (!_shown)
            return;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            if (Opacity > 0.01)
                return;
            Hide();
            _shown = false;
            // Готовые задачи больше не нужны; при следующем показе панель начнётся с чистого листа.
            foreach (var done in _jobs.Where(j => !j.IsRunning).ToList())
                _jobs.Remove(done);
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void Place()
    {
        if (!_shown)
            return;
        var hwnd = new WindowInteropHelper(this).Handle;
        // Правый нижний угол рабочей области того монитора, где курсор.
        var work = Native.WorkAreaAt(Native.CursorPosition());
        Native.GetWindowRect(hwnd, out var rect);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, work.Right - (rect.Right - rect.Left), work.Bottom - (rect.Bottom - rect.Top), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void OnClose(object sender, RoutedEventArgs e) => HidePanel();

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Job job)
            job.Reveal();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Job job)
            job.Cancellation.Cancel();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _hideTimer.Stop();
    }
}

/// <summary>Цвет строки состояния: зелёный — готово, красный — ошибка.</summary>
public sealed class JobStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.FindResource(value switch
        {
            JobState.Done => "SuccessText",
            JobState.Failed => "ErrorText",
            _ => "TextSecondary",
        });

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
