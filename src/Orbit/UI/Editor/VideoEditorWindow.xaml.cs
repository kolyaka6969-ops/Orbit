using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orbit.Core;
using Orbit.Core.Backends;
using Orbit.Shell;

namespace Orbit.UI.Editor;

/// <summary>
/// Видеоредактор из круга инструментов, устроенный как монтажные программы: клипы на шкале времени (обрезка за края,
/// перестановка, разрезание, склейка с другими роликами), ключевые кадры масштаба, позиции, поворота, непрозрачности
/// и громкости, как в After Effects и CapCut, приближение части кадра. Сохраняет VideoRenderer через Media Foundation;
/// исходники не меняются, папку и имя результата выбирают в Проводнике.
/// </summary>
public partial class VideoEditorWindow : Window
{
    private static readonly string VideoFilter =
        "Видео|" + string.Join(";", Formats.Video.Select(e => "*." + e)) + "|Все файлы|*.*";

    private readonly FileItem _source;
    private readonly Settings _settings;
    private readonly string _outputDir;
    private readonly double _bitsPerPixel;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;
    private readonly uint _fpsNumerator;
    private readonly uint _fpsDenominator;
    private readonly List<VideoClip> _clips = [];
    private readonly List<string> _outputs = [];
    private readonly TaskCompletionSource<IReadOnlyList<string>> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closing = new();
    private readonly Dictionary<string, IReadOnlyList<(TimeSpan Time, BitmapSource Image)>> _thumbnails = [];
    private readonly Dictionary<string, float[]> _waves = [];
    private readonly HashSet<string> _loading = [];
    private readonly Stack<(List<VideoClip> Clips, TimeSpan Position)> _undo = new();
    private readonly Stack<(List<VideoClip> Clips, TimeSpan Position)> _redo = new();
    private readonly PropertyRow[] _rows;
    private string? _lastEdit;
    private DateTime _lastEditTime;
    private TimeSpan _position;
    private int _active;
    private string? _loadedPath;
    private TimeSpan? _pendingSeek;
    private bool _playing;
    private bool _zoomEditing;
    private bool _zoomDragging;
    private Point _zoomStart;
    private Rect? _zoomBefore;
    private bool _moving;
    private Point _moveStart;
    private (double X, double Y) _moveOrigin;
    private bool _exporting;
    private bool _finished;

    private VideoEditorWindow(FileItem source, Settings settings, string outputDir, double bitsPerPixel, MediaBackend.ClipInfo info)
    {
        InitializeComponent();
        ThemeManager.UseMica(this);
        _source = source;
        _settings = settings;
        _outputDir = outputDir;
        _bitsPerPixel = bitsPerPixel;
        _canvasWidth = (int)info.Width;
        _canvasHeight = (int)info.Height;
        _fpsNumerator = info.FrameRateNumerator;
        _fpsDenominator = info.FrameRateDenominator;
        Title = $"{source.Name} — видеоредактор · Orbit";
        // Кадр проекта — в пикселях первого ролика: приближение и позиция сразу получаются в этих координатах
        Frame.Width = _canvasWidth;
        Frame.Height = _canvasHeight;
        _clips.Add(NewClip(source.Path, info));

        _rows =
        [
            new PropertyRow(0, "Масштаб", new PropertyRow.Axis("", 10, 400, " %")),
            new PropertyRow(1, "Позиция", new PropertyRow.Axis("X", -_canvasWidth, _canvasWidth, ""), new PropertyRow.Axis("Y", -_canvasHeight, _canvasHeight, "")),
            new PropertyRow(2, "Поворот", new PropertyRow.Axis("", -180, 180, "°")),
            new PropertyRow(3, "Непрозрачность", new PropertyRow.Axis("", 0, 100, " %")),
            new PropertyRow(4, "Громкость", new PropertyRow.Axis("", 0, 200, " %")),
        ];
        foreach (var row in _rows)
        {
            row.KeyAction += OnKeyAction;
            row.Changed += OnPropertyChanged;
            row.DragStarted += lane => Snapshot($"lane{lane}", true);
            row.Done += () => Timeline.Focus();
            Properties.Children.Add(row);
        }

        Timeline.Clips = _clips;
        Timeline.Thumbnails = path => _thumbnails.GetValueOrDefault(path);
        Timeline.Waveform = path => _waves.GetValueOrDefault(path);
        Timeline.Seek += ShowAt;
        Timeline.BeginEdit += () => Snapshot(null);
        Timeline.Edited += OnTimelineEdited;
        Timeline.KeyAction += OnKeyAction;
        Timeline.SplitRequested += _ => Split();
        Timeline.DeleteRequested += DeleteClip;
        Timeline.ResetRequested += ResetClip;
        Timeline.Loaded += (_, _) => Timeline.FitAll();

        PreviewKeyDown += OnKey;
        // Щелчок мимо поля ввода — фокус шкале, чтобы клавиши редактора не печатались в поле
        // (сам Window вернул бы фокус в последнее поле)
        PreviewMouseDown += (_, _) =>
        {
            if (Keyboard.FocusedElement is TextBox { IsMouseOver: false })
                Timeline.Focus();
        };
        Loaded += (_, _) => Timeline.Focus();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrameRendering;
            Player.Close();
            _closing.Cancel();
            _result.TrySetCanceled();
        };
        LoadMedia();
        ShowAt(TimeSpan.Zero);
    }

    private static VideoClip NewClip(string path, MediaBackend.ClipInfo info) => new()
    {
        Path = path,
        Name = System.IO.Path.GetFileName(path),
        Duration = info.Duration,
        Width = info.Width,
        Height = info.Height,
        HasAudio = info.HasAudio,
        Out = info.Duration,
    };

    internal static async Task<IReadOnlyList<string>> EditAsync(IReadOnlyList<FileItem> items, JobContext context)
    {
        var item = items[0];
        var info = await MediaBackend.ProbeClipAsync(item.Path);
        var window = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var editor = new VideoEditorWindow(item, context.Settings, context.OutputDirFor(item), MediaBackend.BitsPerPixel(context.Settings.Video), info);
            editor.Show();
            Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(editor).Handle);
            editor.Activate();
            return editor;
        });
        return await window._result.Task;
    }

    /// <summary>Для «--render»: редактор с двумя клипами, ключами и выбранным ключом.</summary>
    internal static async Task<VideoEditorWindow> ForPreviewAsync(FileItem item)
    {
        var info = await MediaBackend.ProbeClipAsync(item.Path);
        var editor = new VideoEditorWindow(item, new Settings(), item.Folder, 0.08, info);
        var clip = editor._clips[0];
        clip.In = info.Duration * 0.1;
        var second = clip.Clone();
        second.In = info.Duration * 0.5;
        second.Animation.All.ToList().ForEach(v => v.Reset());
        clip.Out = info.Duration * 0.5;
        editor._clips.Add(second);
        var animation = clip.Animation;
        animation.Scale.AddKey(clip.In);
        animation.Scale.AddKey(clip.In + (clip.Out - clip.In) * 0.7).A = 1.6;
        animation.Rotation.AddKey(clip.In);
        animation.Rotation.AddKey(clip.In + (clip.Out - clip.In) * 0.7).A = 12;
        animation.Opacity.AddKey(clip.In).A = 0;
        animation.Opacity.AddKey(clip.In + (clip.Out - clip.In) * 0.25).Ease = KeyEase.Smooth;
        animation.Position.Static = (120, -40);
        await Task.WhenAll(editor._clips.Select(c => c.Path).Distinct().Select(editor.LoadMediaAsync));
        editor.Timeline.SelectedKey = animation.Scale.Keys[1];
        editor.ShowAt(clip.Length * 0.6);
        editor.Timeline.Width = 1250;
        editor.Timeline.Measure(new Size(1250, editor.Timeline.Height));
        editor.Timeline.Arrange(new Rect(0, 0, 1250, editor.Timeline.Height));
        editor.Timeline.FitAll();
        editor._finished = true;
        return editor;
    }

    private double Scale => Fit.ActualWidth > 0 ? _canvasWidth / Fit.ActualWidth : 1;

    private void OnFitSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Zoom.Scale = Scale;
        Zoom.InvalidateVisual();
    }

    private VideoClip Active => _clips[_active];

    private TimeSpan Total => _clips.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Length);

    private TimeSpan ClipStart(int index) => _clips.Take(index).Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Length);

    /// <summary>Время бегунка в файле клипа под ним.</summary>
    private TimeSpan SourceTime => Active.In + (_position - ClipStart(_active));

    // ---------------------------------------------------------------- миниатюры и звук для шкалы

    private void LoadMedia()
    {
        foreach (var path in _clips.Select(c => c.Path).Distinct())
            _ = LoadMediaAsync(path);
    }

    private async Task LoadMediaAsync(string path)
    {
        if (!_loading.Add(path))
            return;
        var clip = _clips.First(c => c.Path == path);
        try
        {
            var thumbs = VideoRenderer.ThumbnailsAsync(path, clip.Duration, 72, _closing.Token);
            var wave = clip.HasAudio ? VideoRenderer.WaveformAsync(path, TimelineView.WavePerSecond, _closing.Token) : Task.FromResult<float[]>([]);
            _thumbnails[path] = await thumbs;
            Timeline.InvalidateVisual();
            _waves[path] = await wave;
            Timeline.InvalidateVisual();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // без миниатюр шкала всё равно работает
        }
    }

    // ---------------------------------------------------------------- бегунок и просмотр

    /// <summary>Перейти на место шкалы: выбрать клип под бегунком, открыть его файл, показать кадр с его ключами.</summary>
    private void ShowAt(TimeSpan time)
    {
        var total = Total;
        _position = time < TimeSpan.Zero ? TimeSpan.Zero : time > total ? total : time;
        var start = TimeSpan.Zero;
        _active = _clips.Count - 1;
        for (var i = 0; i < _clips.Count; i++)
        {
            if (_position < start + _clips[i].Length)
            {
                _active = i;
                break;
            }
            start += _clips[i].Length;
        }
        var source = SourceTime;
        if (source >= Active.Out)
            source = Active.Out - TimeSpan.FromMilliseconds(30);
        if (_loadedPath != Active.Path)
        {
            _loadedPath = Active.Path;
            _pendingSeek = source;
            Player.Source = new Uri(Active.Path);
            // Ручной режим: файл открывается при Play; на паузе виден кадр
            Player.Play();
            if (!_playing)
                Player.Pause();
        }
        else if (_pendingSeek is not null)
            _pendingSeek = source;
        else if (!_playing || (Player.Position - source).Duration() > TimeSpan.FromMilliseconds(150))
            Player.Position = source;
        Timeline.Selected = _active;
        Timeline.Position = _position;
        UpdatePreview();
        UpdateInspector();
        UpdateTexts();
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (_pendingSeek is { } seek)
            Player.Position = seek;
        _pendingSeek = null;
        if (_playing)
            Player.Play();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (_playing)
            NextClipOrStop();
    }

    /// <summary>Кадр проекта в просмотре — той же матрицей, что и при сохранении.</summary>
    private void UpdatePreview()
    {
        var clip = Active;
        var time = SourceTime;
        Player.Width = clip.Width;
        Player.Height = clip.Height;
        Player.RenderTransform = new MatrixTransform(clip.FrameMatrix(time, _canvasWidth, _canvasHeight, crop: !_zoomEditing));
        Player.Opacity = Math.Clamp(clip.Animation.Opacity.At(time).A, 0, 1);
        Player.Volume = Math.Clamp(clip.Animation.Volume.At(time).A, 0, 1);
        Player.IsMuted = MuteToggle.IsChecked == true;
        Zoom.Area = clip.Crop;
        ZoomText.Text = _zoomEditing ? "Готово" : clip.Crop is null ? "Приблизить" : "Изменить приближение";
        ResetZoomButton.Visibility = clip.Crop is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPlay(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_playing)
        {
            Pause();
            return;
        }
        if (_position >= Total - TimeSpan.FromMilliseconds(80))
            ShowAt(TimeSpan.Zero);
        _playing = true;
        Player.Play();
        PlayButton.Content = "";
        CompositionTarget.Rendering += OnFrameRendering;
    }

    private void Pause()
    {
        if (!_playing)
            return;
        _playing = false;
        Player.Pause();
        PlayButton.Content = "";
        CompositionTarget.Rendering -= OnFrameRendering;
        ShowAt(_position);
    }

    /// <summary>Во время воспроизведения: бегунок за видео, ключи — на каждом кадре, на конце клипа — следующий.</summary>
    private void OnFrameRendering(object? sender, EventArgs e)
    {
        if (!_playing || _pendingSeek is not null)
            return;
        var source = Player.Position;
        if (source >= Active.Out)
        {
            NextClipOrStop();
            return;
        }
        if (source < Active.In)
            source = Active.In;
        _position = ClipStart(_active) + (source - Active.In);
        Timeline.Position = _position;
        UpdatePreview();
        UpdateInspector();
        UpdateTexts();
    }

    private void NextClipOrStop()
    {
        var next = ClipStart(_active) + Active.Length;
        if (_active < _clips.Count - 1)
        {
            ShowAt(next);
            if (_pendingSeek is null)
                Player.Position = Active.In;
            Player.Play();
            return;
        }
        _position = Total;
        Pause();
    }

    private void Step(double seconds)
    {
        Pause();
        ShowAt(_position + TimeSpan.FromSeconds(seconds));
    }

    private static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.ff") : time.ToString(@"m\:ss\.ff");

    private void UpdateTexts()
    {
        TimeText.Text = $"{Format(_position)} / {Format(Total)}";
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        DeleteButton.IsEnabled = _clips.Count > 1;
        if (_exporting)
            return;
        HintText.Text = _zoomEditing
            ? "Обведите мышью часть кадра — она заполнит весь кадр. Esc — выйти"
            : Timeline.SelectedKey is not null
                ? "Ключ выбран: тяните его по дорожке, Delete — удалить, правый щелчок — вид перехода"
                : "Тяните кадр мышью — позиция, Ctrl + колесо над кадром — масштаб. Ромб ◆ ставит ключ: дальше правки на бегунке становятся ключами. S — разрезать, K — ключ всем";
    }

    // ---------------------------------------------------------------- панель свойств и ключи

    private void UpdateInspector()
    {
        var clip = Active;
        var time = SourceTime;
        ClipName.Text = $"{clip.Name} · {Format(clip.In)} – {Format(clip.Out)}";
        for (var lane = 0; lane < _rows.Length; lane++)
        {
            var value = TimelineView.Lane(clip, lane);
            var (a, b) = value.At(time);
            double[] shown = lane switch
            {
                0 or 3 or 4 => [a * 100],
                1 => [a, b],
                _ => [a],
            };
            _rows[lane].Show(shown, value.KeyAt(time), value.IsAnimated);
        }
        var here = clip.Animation.All.Select(v => v.KeyAt(time)).FirstOrDefault(k => k is not null);
        EasePanel.IsEnabled = here is not null;
        EaseLinear.IsChecked = here?.Ease == KeyEase.Linear;
        EaseSmooth.IsChecked = here?.Ease == KeyEase.Smooth;
        EaseHold.IsChecked = here?.Ease == KeyEase.Hold;
    }

    private void OnPropertyChanged(int lane, double[] values)
    {
        Snapshot($"lane{lane}");
        var value = TimelineView.Lane(Active, lane);
        var time = SourceTime;
        switch (lane)
        {
            case 0:
                value.Set(time, Math.Clamp(values[0], 1, 2000) / 100);
                break;
            case 1:
                value.Set(time, values[0], values[1]);
                break;
            case 2:
                value.Set(time, values[0]);
                break;
            default:
                value.Set(time, Math.Clamp(values[0], 0, lane == 3 ? 100 : 200) / 100);
                break;
        }
        AfterEdit();
    }

    private void OnKeyAction(int lane, KeyCommand command)
    {
        Pause();
        var clip = Active;
        var time = SourceTime;
        var value = TimelineView.Lane(clip, lane);
        switch (command)
        {
            case KeyCommand.Toggle when value.KeyAt(time) is { } key:
                Snapshot(null);
                value.Remove(key);
                if (ReferenceEquals(Timeline.SelectedKey, key))
                    Timeline.SelectedKey = null;
                break;
            case KeyCommand.Toggle:
            case KeyCommand.Add:
                Snapshot(null);
                Timeline.SelectedKey = value.AddKey(time);
                break;
            case KeyCommand.Previous or KeyCommand.Next:
                var keys = value.Keys.Where(k => k.Time >= clip.In && k.Time <= clip.Out);
                var target = command == KeyCommand.Previous
                    ? keys.LastOrDefault(k => k.Time < time - AnimatedValue.Snap)
                    : keys.FirstOrDefault(k => k.Time > time + AnimatedValue.Snap);
                if (target is not null)
                {
                    Timeline.SelectedKey = target;
                    ShowAt(ClipStart(_active) + (target.Time - clip.In));
                }
                return;
        }
        AfterEdit();
    }

    private void OnKeyAll(object sender, RoutedEventArgs e) => KeyAll();

    /// <summary>Как кнопка ключа в CapCut: ключ всем свойствам в месте бегунка.</summary>
    private void KeyAll()
    {
        Pause();
        Snapshot(null);
        var time = SourceTime;
        foreach (var value in Active.Animation.All)
            value.AddKey(time);
        AfterEdit();
    }

    private void OnEase(object sender, RoutedEventArgs e)
    {
        var ease = EaseSmooth.IsChecked == true ? KeyEase.Smooth : EaseHold.IsChecked == true ? KeyEase.Hold : KeyEase.Linear;
        Snapshot(null);
        foreach (var key in Active.Animation.All.Select(v => v.KeyAt(SourceTime)).OfType<Keyframe>())
            key.Ease = ease;
        AfterEdit();
    }

    private void OnResetClip(object sender, RoutedEventArgs e) => ResetClip(_active);

    private void ResetClip(int index)
    {
        Snapshot(null);
        _clips[index].Crop = null;
        foreach (var value in _clips[index].Animation.All)
            value.Reset();
        Timeline.SelectedKey = null;
        AfterEdit();
    }

    /// <summary>После любой правки — перерисовать всё, что от неё зависит.</summary>
    private void AfterEdit()
    {
        Timeline.Clips = _clips;
        Timeline.Refresh();
        ShowAt(_position);
    }

    private void OnTimelineEdited()
    {
        UpdatePreview();
        UpdateInspector();
        UpdateTexts();
    }

    // ---------------------------------------------------------------- позиция и масштаб мышью прямо в кадре

    private void OnFrameDown(object sender, MouseButtonEventArgs e)
    {
        if (_zoomEditing || _exporting)
            return;
        Pause();
        Snapshot(null);
        _moving = true;
        _moveStart = e.GetPosition(Frame);
        _moveOrigin = Active.Animation.Position.At(SourceTime);
        Frame.CaptureMouse();
        e.Handled = true;
    }

    private void OnFrameMove(object sender, MouseEventArgs e)
    {
        if (!_moving)
            return;
        var point = e.GetPosition(Frame);
        Active.Animation.Position.Set(SourceTime, Math.Round(_moveOrigin.X + point.X - _moveStart.X), Math.Round(_moveOrigin.Y + point.Y - _moveStart.Y));
        UpdatePreview();
        UpdateInspector();
        Timeline.Refresh();
    }

    private void OnFrameUp(object sender, MouseButtonEventArgs e) => Frame.ReleaseMouseCapture();

    private void OnFrameLostCapture(object sender, MouseEventArgs e)
    {
        if (_moving)
        {
            _moving = false;
            AfterEdit();
        }
    }

    private void OnFrameWheel(object sender, MouseWheelEventArgs e)
    {
        if (_zoomEditing || _exporting || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        Pause();
        Snapshot("wheel");
        var value = Active.Animation.Scale;
        var scale = value.At(SourceTime).A * (e.Delta > 0 ? 1.05 : 1 / 1.05);
        value.Set(SourceTime, Math.Clamp(Math.Round(scale, 3), 0.01, 20));
        AfterEdit();
        e.Handled = true;
    }

    // ---------------------------------------------------------------- приближение

    private Point ClampToFrame(Point point) => new(Math.Clamp(point.X, 0, _canvasWidth), Math.Clamp(point.Y, 0, _canvasHeight));

    private void OnZoom(object sender, RoutedEventArgs e) => SetZoomEditing(!_zoomEditing);

    /// <summary>Выбор области: виден весь кадр клипа и рамка; вне выбора — приближенная часть, как в результате.</summary>
    private void SetZoomEditing(bool editing)
    {
        if (editing)
            Pause();
        _zoomEditing = editing;
        Zoom.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        Zoom.Scale = Scale;
        if (editing)
            ZoomButton.Style = (Style)FindResource("AccentButton");
        else
            ZoomButton.ClearValue(StyleProperty);
        UpdatePreview();
        UpdateTexts();
    }

    private void OnZoomDown(object sender, MouseButtonEventArgs e)
    {
        _zoomBefore = Active.Crop;
        _zoomStart = ClampToFrame(e.GetPosition(Zoom));
        _zoomDragging = true;
        Zoom.Scale = Scale;
        Zoom.CaptureMouse();
        e.Handled = true;
    }

    private void OnZoomMove(object sender, MouseEventArgs e)
    {
        if (!_zoomDragging)
            return;
        Zoom.Area = ZoomRect(ClampToFrame(e.GetPosition(Zoom)));
        e.Handled = true;
    }

    /// <summary>Область тех же пропорций, что и кадр: приближенное видео не растягивается и не получает полос.</summary>
    private Rect ZoomRect(Point to)
    {
        var aspect = (double)_canvasWidth / _canvasHeight;
        var dx = to.X - _zoomStart.X;
        var dy = to.Y - _zoomStart.Y;
        var width = Math.Max(Math.Abs(dx), Math.Abs(dy) * aspect);
        // Не выходим за кадр в ту сторону, куда тянут
        var room = Math.Min(dx >= 0 ? _canvasWidth - _zoomStart.X : _zoomStart.X, (dy >= 0 ? _canvasHeight - _zoomStart.Y : _zoomStart.Y) * aspect);
        width = Math.Min(width, room);
        var height = width / aspect;
        var x = dx >= 0 ? _zoomStart.X : _zoomStart.X - width;
        var y = dy >= 0 ? _zoomStart.Y : _zoomStart.Y - height;
        return new Rect(x, y, width, height);
    }

    private void OnZoomUp(object sender, MouseButtonEventArgs e) => Zoom.ReleaseMouseCapture();

    private void OnZoomLostCapture(object sender, MouseEventArgs e)
    {
        if (!_zoomDragging)
            return;
        _zoomDragging = false;
        // Щелчок без протяжки или совсем маленькая область — остаётся прежняя
        if (Zoom.Area is not { } area || area.Width < Math.Max(64, _canvasWidth * 0.05))
        {
            Zoom.Area = _zoomBefore;
            return;
        }
        Snapshot(null);
        Active.Crop = area;
        SetZoomEditing(false);
        AfterEdit();
    }

    private void OnResetZoom(object sender, RoutedEventArgs e)
    {
        Snapshot(null);
        Active.Crop = null;
        SetZoomEditing(false);
        AfterEdit();
    }

    // ---------------------------------------------------------------- монтаж: разрезать, удалить, обрезать, добавить

    private void OnSplit(object sender, RoutedEventArgs e) => Split();

    /// <summary>Клип под бегунком становится двумя; ключи остаются у обоих — анимация не прерывается.</summary>
    private void Split()
    {
        Pause();
        var clip = Active;
        var time = SourceTime;
        if (time - clip.In < TimeSpan.FromSeconds(0.1) || clip.Out - time < TimeSpan.FromSeconds(0.1))
        {
            HintText.Text = "Слишком близко к краю клипа — сдвиньте бегунок";
            return;
        }
        Snapshot(null);
        var right = clip.Clone();
        clip.Out = time;
        right.In = time;
        _clips.Insert(_active + 1, right);
        Timeline.SelectedKey = null;
        AfterEdit();
    }

    private void OnDeleteClip(object sender, RoutedEventArgs e) => DeleteClip(_active);

    private void DeleteClip(int index)
    {
        if (_clips.Count < 2)
            return;
        Pause();
        Snapshot(null);
        var start = ClipStart(index);
        _clips.RemoveAt(index);
        Timeline.SelectedKey = null;
        _position = start;
        AfterEdit();
    }

    private void OnSetStart(object sender, RoutedEventArgs e) => TrimToPlayhead(true);

    private void OnSetEnd(object sender, RoutedEventArgs e) => TrimToPlayhead(false);

    private void TrimToPlayhead(bool start)
    {
        Pause();
        var clip = Active;
        var time = SourceTime;
        if ((start ? clip.Out - time : time - clip.In) < TimeSpan.FromSeconds(0.1))
            return;
        Snapshot(null);
        if (start)
        {
            clip.In = time;
            _position = ClipStart(_active);
        }
        else
            clip.Out = time;
        AfterEdit();
    }

    private void OnAddVideo(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Какие видео добавить на шкалу?",
            Filter = VideoFilter,
            Multiselect = true,
            InitialDirectory = _source.Folder,
        };
        if (dialog.ShowDialog(this) == true)
            _ = AddVideosAsync(dialog.FileNames);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!_exporting && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            _ = AddVideosAsync(files.Where(f => new FileItem(f).Kind == FileKind.Video).ToList());
    }

    private async Task AddVideosAsync(IReadOnlyList<string> paths)
    {
        var added = new List<VideoClip>();
        foreach (var path in paths)
        {
            try
            {
                added.Add(NewClip(path, await MediaBackend.ProbeClipAsync(path)));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Не удалось открыть «{System.IO.Path.GetFileName(path)}»: {ex.Message}", "Orbit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        if (added.Count == 0)
            return;
        Snapshot(null);
        _clips.AddRange(added);
        LoadMedia();
        Timeline.FitAll();
        AfterEdit();
    }

    // ---------------------------------------------------------------- отмена

    /// <summary>Запомнить состояние перед правкой. Правки одного ползунка подряд — одна отмена.</summary>
    private void Snapshot(string? group, bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && group is not null && group == _lastEdit && now - _lastEditTime < TimeSpan.FromSeconds(1))
        {
            _lastEditTime = now;
            return;
        }
        _lastEdit = group;
        _lastEditTime = now;
        _undo.Push((_clips.Select(c => c.Clone()).ToList(), _position));
        _redo.Clear();
    }

    private void OnUndo(object sender, RoutedEventArgs e) => Undo(_undo, _redo);

    private void OnRedo(object sender, RoutedEventArgs e) => Undo(_redo, _undo);

    private void Undo(Stack<(List<VideoClip> Clips, TimeSpan Position)> from, Stack<(List<VideoClip> Clips, TimeSpan Position)> to)
    {
        if (from.Count == 0 || _exporting)
            return;
        Pause();
        to.Push((_clips.Select(c => c.Clone()).ToList(), _position));
        var (clips, position) = from.Pop();
        _clips.Clear();
        _clips.AddRange(clips);
        _lastEdit = null;
        Timeline.SelectedKey = null;
        _position = position;
        AfterEdit();
    }

    // ---------------------------------------------------------------- шкала: масштаб

    private void OnTimelineIn(object sender, RoutedEventArgs e) => Timeline.ZoomBy(1.5);

    private void OnTimelineOut(object sender, RoutedEventArgs e) => Timeline.ZoomBy(1 / 1.5);

    private void OnTimelineFit(object sender, RoutedEventArgs e) => Timeline.FitAll();

    private void OnMuteChanged(object sender, RoutedEventArgs e) => Player.IsMuted = MuteToggle.IsChecked == true;

    // ---------------------------------------------------------------- сохранение

    private bool Changed =>
        _clips.Count != 1 || _clips[0].Path != _source.Path || _clips[0].IsEdited || MuteToggle.IsChecked == true || SizeOriginal.IsChecked != true;

    private string SaveFolder =>
        _settings.VideoSaveFolder is { } folder && Directory.Exists(folder) ? folder : _outputDir;

    private VideoProject Project() => new(_clips.Select(c => c.Clone()).ToList(), _canvasWidth, _canvasHeight, _fpsNumerator, _fpsDenominator,
        MuteToggle.IsChecked == true, Size1080.IsChecked == true ? 1080u : Size720.IsChecked == true ? 720u : 0u, _bitsPerPixel);

    private async void OnSaveFrame(object sender, RoutedEventArgs e)
    {
        Pause();
        var clip = Active.Clone();
        var time = SourceTime;
        FrameButton.IsEnabled = false;
        try
        {
            var bitmap = await VideoRenderer.FrameAsync(clip, time, _canvasWidth, _canvasHeight, _closing.Token);
            var path = await OutputPaths.ProduceAsync(SaveFolder, $"{_source.BaseName} (кадр {Format(_position).Replace(':', '-')})", "png", temp =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(temp);
                encoder.Save(stream);
                return Task.CompletedTask;
            });
            _outputs.Add(path);
            HintText.Text = $"Кадр сохранён: {path}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MessageBox.Show(this, $"Не удалось сохранить кадр: {ex.Message}", "Orbit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            FrameButton.IsEnabled = !_exporting;
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync();

    /// <summary>Папка и имя — в окне Проводника «Сохранить как»; затем рендер во временный файл рядом и замена.</summary>
    private async Task<bool> SaveAsync()
    {
        if (_exporting)
            return false;
        Pause();
        var joined = _clips.Select(c => c.Path).Distinct().Count() > 1;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Куда сохранить видео?",
            Filter = "Видео MP4|*.mp4",
            DefaultExt = "mp4",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = SaveFolder,
            FileName = System.IO.Path.GetFileName(OutputPaths.Unique(SaveFolder, $"{_source.BaseName} ({(joined ? "склейка" : "изменено")})", "mp4")),
        };
        if (dialog.ShowDialog(this) != true)
            return false;
        var destination = dialog.FileName;
        if (_clips.Any(c => string.Equals(System.IO.Path.GetFullPath(c.Path), System.IO.Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Это файл из проекта — его нельзя перезаписать, пока он открыт. Выберите другое имя.", "Orbit",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var folder = System.IO.Path.GetDirectoryName(destination)!;
        _settings.VideoSaveFolder = folder;
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не запомним папку — не беда
        }

        SetExporting(true);
        var progress = new Progress<double>(value =>
        {
            Progress.Value = value;
            ProgressText.Text = $"{value:P0}";
        });
        var temp = System.IO.Path.Combine(folder, $".orbit-{Guid.NewGuid():N}.mp4");
        try
        {
            await VideoRenderer.RenderAsync(Project(), temp, progress, _closing.Token);
            File.Move(temp, destination, overwrite: true);
            _outputs.Add(destination);
            _finished = true;
            _result.TrySetResult(_outputs);
            Close();
            return true;
        }
        catch (OperationCanceledException)
        {
            // окно закрыли во время сохранения
            return false;
        }
        catch (Exception ex)
        {
            SetExporting(false);
            MessageBox.Show(this, $"Не удалось сохранить видео: {ex.Message}", "Orbit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            OutputPaths.TryDelete(temp);
        }
    }

    private void SetExporting(bool exporting)
    {
        _exporting = exporting;
        Options.IsEnabled = SaveButton.IsEnabled = FrameButton.IsEnabled = Timeline.IsEnabled = Frame.IsEnabled =
            Properties.IsEnabled = AddVideoButton.IsEnabled = !exporting;
        ProgressPanel.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        Progress.Value = 0;
        ProgressText.Text = "0 %";
        HintText.Text = exporting ? "Сохраняю видео… Закройте окно, чтобы прервать" : "";
        if (!exporting)
            UpdateTexts();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_finished)
            return;
        if (_exporting)
        {
            e.Cancel = MessageBox.Show(this, "Прервать сохранение видео?", "Orbit", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes;
            return;
        }
        if (!Changed)
        {
            // Сохраняли только кадры — это и есть результат
            _finished = true;
            _result.TrySetResult(_outputs);
            return;
        }
        var answer = MessageBox.Show(this, "Сохранить видео с правками? Исходные файлы не изменятся.", "Orbit",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            e.Cancel = true;
            _ = SaveAsync();
        }
        else if (answer == MessageBoxResult.Cancel)
            e.Cancel = true;
        else
        {
            _finished = true;
            _result.TrySetResult(_outputs);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_exporting || Keyboard.FocusedElement is TextBox)
            return;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Space:
                TogglePlay();
                break;
            case Key.Left:
                Step(shift ? -1 : -(double)_fpsDenominator / _fpsNumerator);
                break;
            case Key.Right:
                Step(shift ? 1 : (double)_fpsDenominator / _fpsNumerator);
                break;
            case Key.Home:
                Pause();
                ShowAt(TimeSpan.Zero);
                break;
            case Key.End:
                Pause();
                ShowAt(Total);
                break;
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                Undo(_redo, _undo);
                break;
            case Key.Z when ctrl:
                Undo(_undo, _redo);
                break;
            case Key.S when ctrl:
                _ = SaveAsync();
                break;
            case Key.S:
                Split();
                break;
            case Key.K:
                KeyAll();
                break;
            case Key.I:
                TrimToPlayhead(true);
                break;
            case Key.O:
                TrimToPlayhead(false);
                break;
            case Key.Delete when Timeline.SelectedKey is { } key:
                Snapshot(null);
                foreach (var value in Active.Animation.All)
                    value.Remove(key);
                Timeline.SelectedKey = null;
                AfterEdit();
                break;
            case Key.Delete:
                DeleteClip(_active);
                break;
            case Key.OemPlus or Key.Add when ctrl:
                Timeline.ZoomBy(1.5);
                break;
            case Key.OemMinus or Key.Subtract when ctrl:
                Timeline.ZoomBy(1 / 1.5);
                break;
            case Key.Escape when _zoomEditing:
                SetZoomEditing(false);
                break;
            case Key.Escape when Timeline.SelectedKey is not null:
                Timeline.SelectedKey = null;
                Timeline.Refresh();
                UpdateTexts();
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
