using System.Windows.Threading;
using Orbit.Core;
using Orbit.Shell;

namespace Orbit.UI;

/// <summary>Решает, когда открыть круг (жест при перетаскивании, горячая клавиша, контекстное меню) и с какими пунктами.</summary>
internal sealed class WheelController
{
    private readonly Settings _settings;
    private readonly JobRunner _jobs;
    private readonly GlobalInput _input;
    private readonly IReadOnlyList<Operation> _operations = Catalog.Build();
    private readonly List<string> _pendingPaths = [];
    private readonly DispatcherTimer _argsTimer;
    private WheelWindow? _wheel;

    public WheelController(Settings settings, JobRunner jobs, GlobalInput input)
    {
        _settings = settings;
        _jobs = jobs;
        _input = input;

        // Проводник запускает по процессу на каждый выделенный файл — собираем пути, пришедшие почти одновременно.
        _argsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _argsTimer.Tick += (_, _) =>
        {
            _argsTimer.Stop();
            var paths = _pendingPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _pendingPaths.Clear();
            OpenFor(paths);
        };

        input.IsWheelKey = key => !_settings.Paused && _settings.WheelFor(key) is not null;
        input.ModifierTapped += OnModifierTapped;
        input.LeftButtonUp += () => _wheel?.OnDragEnded();
        input.MouseDown += point =>
        {
            if (_wheel is { IsFinished: false, Mode: WheelMode.Click } wheel && !wheel.Contains(point))
                wheel.Finish();
        };
        input.EscapePressed += () =>
        {
            if (_wheel is { IsFinished: false, Mode: WheelMode.Drag } wheel)
                wheel.Finish();
        };
    }

    public IReadOnlyList<Operation> Operations => _operations;

    private IReadOnlyList<Operation> OperationsFor(WheelKind wheel, IReadOnlyList<FileItem> items) =>
        _operations.Where(o => o.Wheel == wheel && o.AppliesTo(items)).ToList();

    private void OnModifierTapped(ModifierKey key)
    {
        if (_wheel is { IsFinished: false } open)
        {
            if (!_settings.Paused && _settings.WheelFor(key) is { } next)
                open.SwitchTo(next);
            return;
        }
        if (!_settings.Paused && _settings.WheelFor(key) is { } wheel && _input.IsDragging)
            Open(WheelMode.Drag, wheel, null, _input.Cursor);
        else
            _input.WheelActive = _wheel is not null;
    }

    /// <summary>Ctrl + Alt + Пробел: файлы, выделенные в Проводнике (или скопированные в буфер обмена).</summary>
    public void OnHotkey()
    {
        if (_settings.Paused)
            return;
        var paths = ExplorerIntegration.SelectedFiles(Native.GetForegroundWindow());
        if (paths.Count == 0)
        {
            Notify("Ничего не выбрано", "Выделите файлы в Проводнике или скопируйте их (Ctrl+C) и нажмите Ctrl + Alt + Пробел ещё раз.");
            return;
        }
        OpenFor(paths);
    }

    public void OpenForClipboard()
    {
        var paths = ExplorerIntegration.FromClipboard();
        if (paths.Count == 0)
        {
            Notify("Буфер обмена пуст", "Скопируйте файлы или изображение, например снимок экрана (Win+Shift+S).");
            return;
        }
        OpenFor(paths);
    }

    /// <summary>Пути из контекстного меню Проводника (каждый файл может прийти отдельным вызовом).</summary>
    public void QueuePaths(IEnumerable<string> paths)
    {
        _pendingPaths.AddRange(paths);
        _argsTimer.Stop();
        _argsTimer.Start();
    }

    public void OpenFor(IReadOnlyList<string> paths)
    {
        var items = paths.Where(p => File.Exists(p) || Directory.Exists(p)).Select(p => new FileItem(p)).ToList();
        if (items.Count == 0)
            return;
        Open(WheelMode.Click, WheelKind.Convert, items, Native.CursorPosition());
    }

    private void Open(WheelMode mode, WheelKind wheel, IReadOnlyList<FileItem>? items, Native.POINT at)
    {
        _wheel?.Finish();
        var window = new WheelWindow(mode, wheel, items, OperationsFor, (op, targets) => _jobs.Start(op, targets));
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_wheel, window))
                return;
            _wheel = null;
            _input.WheelActive = false;
        };
        _wheel = window;
        _input.WheelActive = true;
        window.ShowAt(at);
        if (mode == WheelMode.Click)
            Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(window).Handle);
    }

    /// <summary>Сообщение без операции — через ту же панель в углу экрана.</summary>
    public event Action<string, string>? Notification;

    private void Notify(string title, string text) => Notification?.Invoke(title, text);
}
