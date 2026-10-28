using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orbit.Core;

namespace Orbit.UI.Editor;

/// <summary>Что сделать с ключами свойства на бегунке (кнопки в заголовке дорожки и двойной щелчок).</summary>
internal enum KeyCommand
{
    Previous,
    Toggle,
    Next,
    Add,
}

/// <summary>
/// Шкала времени как в монтажных программах: линейка с делениями, дорожка видео с клипами (кадры и волна звука),
/// под ней — дорожки свойств выбранного клипа с ромбами ключей, как в After Effects. Клипы обрезают за края, переставляют
/// перетаскиванием, ключи двигают мышью; Ctrl + колесо — масштаб шкалы, колесо — прокрутка.
/// Сама шкала только рисует и сообщает о правках; что выбрано и где бегунок, решает окно.
/// </summary>
internal sealed class TimelineView : FrameworkElement
{
    public const double HeaderWidth = 196;
    private const double Pad = 12;
    private const double RulerHeight = 28;
    private const double TrackTop = RulerHeight + 6;
    private const double TrackHeight = 66;
    private const double WaveHeight = 18;
    private const double LaneTop = TrackTop + TrackHeight + 8;
    private const double LaneHeight = 24;
    private const double ScrollHeight = 10;
    private const double Edge = 7;
    private static readonly TimeSpan MinLength = TimeSpan.FromSeconds(0.1);

    public static readonly string[] LaneNames = ["Масштаб", "Позиция", "Поворот", "Непрозрачность", "Громкость"];

    private enum Drag
    {
        None,
        Playhead,
        TrimIn,
        TrimOut,
        Clip,
        Key,
        Scroll,
    }

    private readonly DrawingVisual _overlay = new();
    private Drag _drag;
    private int _dragClip;
    private double _downX;
    private TimeSpan _origin;
    private TimeSpan _grabOffset;
    private bool _changed;
    private AnimatedValue? _dragValue;
    private double _pps = 60;
    private double _scroll;
    private TimeSpan _position;

    public TimelineView()
    {
        Height = LaneTop + LaneNames.Length * LaneHeight + 8 + ScrollHeight;
        ClipToBounds = true;
        // Держит фокус клавиатуры редактора, пока не печатают в поле
        Focusable = true;
        FocusVisualStyle = null;
        AddVisualChild(_overlay);
    }

    public IReadOnlyList<VideoClip> Clips { get; set; } = [];

    private int _selected;

    /// <summary>Клип под бегунком — его ключи показаны на дорожках свойств.</summary>
    public int Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            InvalidateVisual();
        }
    }

    public Keyframe? SelectedKey { get; set; }

    /// <summary>Миниатюры файла, когда готовы.</summary>
    public Func<string, IReadOnlyList<(TimeSpan Time, BitmapSource Image)>?>? Thumbnails { get; set; }

    /// <summary>Волна звука файла (50 значений в секунду), когда готова.</summary>
    public Func<string, float[]?>? Waveform { get; set; }

    public const int WavePerSecond = 50;

    public TimeSpan Position
    {
        get => _position;
        set
        {
            _position = value;
            if (_drag == Drag.None)
                KeepVisible();
            DrawOverlay();
        }
    }

    public event Action<TimeSpan>? Seek;

    /// <summary>Сейчас начнётся правка — запомнить для отмены.</summary>
    public event Action? BeginEdit;

    /// <summary>Клипы или ключи изменились.</summary>
    public event Action? Edited;

    public event Action<int, KeyCommand>? KeyAction;

    public event Action<int>? SplitRequested;

    public event Action<int>? DeleteRequested;

    public event Action<int>? ResetRequested;

    public static AnimatedValue Lane(VideoClip clip, int lane) => lane switch
    {
        0 => clip.Animation.Scale,
        1 => clip.Animation.Position,
        2 => clip.Animation.Rotation,
        3 => clip.Animation.Opacity,
        _ => clip.Animation.Volume,
    };

    public TimeSpan Total => Clips.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Length);

    public TimeSpan ClipStart(int index) => Clips.Take(index).Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Length);

    /// <summary>Время на шкале → время в файле выбранного клипа.</summary>
    private TimeSpan SourceTime(TimeSpan time) => Clips[Selected].In + (time - ClipStart(Selected));

    private double ContentWidth => Math.Max(1, ActualWidth - HeaderWidth);

    private double X(TimeSpan time) => HeaderWidth + Pad + time.TotalSeconds * _pps - _scroll;

    private TimeSpan At(double x) => TimeSpan.FromSeconds(Math.Max(0, (x - HeaderWidth - Pad + _scroll) / _pps));

    private double MaxScroll => Math.Max(0, Total.TotalSeconds * _pps + 60 + Pad - ContentWidth);

    /// <summary>Весь проект в ширину шкалы.</summary>
    public void FitAll()
    {
        if (ActualWidth <= HeaderWidth || Total <= TimeSpan.Zero)
            return;
        _pps = Math.Clamp((ContentWidth - 40 - Pad) / Total.TotalSeconds, 2, 600);
        _scroll = 0;
        Refresh();
    }

    /// <summary>Масштаб шкалы: больше 1 — крупнее.</summary>
    public void ZoomBy(double factor) => ZoomAround(X(_position) is var x && x >= HeaderWidth && x <= ActualWidth ? x : HeaderWidth + ContentWidth / 2, factor);

    private void ZoomAround(double x, double factor)
    {
        var time = At(x).TotalSeconds;
        _pps = Math.Clamp(_pps * factor, 2, 600);
        _scroll = Math.Clamp(time * _pps - (x - HeaderWidth - Pad), 0, MaxScroll);
        Refresh();
    }

    public void Refresh()
    {
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        InvalidateVisual();
        DrawOverlay();
    }

    private void KeepVisible()
    {
        var x = X(_position);
        if (x < HeaderWidth || x > ActualWidth - 24)
        {
            _scroll = Math.Clamp(_position.TotalSeconds * _pps - ContentWidth * 0.1, 0, MaxScroll);
            InvalidateVisual();
        }
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _overlay;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Refresh();
    }

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private static Brush Alpha(Brush brush, byte alpha)
    {
        var color = brush is SolidColorBrush solid ? solid.Color : Colors.Gray;
        var result = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        result.Freeze();
        return result;
    }

    private static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private FormattedText Text(string text, double size, Brush brush, bool bold = false, bool icon = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(icon ? IconFont : UiFont, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ---------------------------------------------------------------- рисование

    protected override void OnRender(DrawingContext dc)
    {
        var text = Res("TextPrimary");
        var secondary = Res("TextSecondary");
        var tertiary = Res("TextTertiary");
        var accent = Res("AccentBrush");
        var divider = new Pen(Res("DividerStroke"), 1);
        double width = ActualWidth, height = ActualHeight;

        dc.DrawRectangle(Res("ControlAltFill"), null, new Rect(0, 0, width, height));
        dc.DrawRectangle(Res("CardFill"), null, new Rect(0, 0, HeaderWidth, height));
        dc.DrawLine(divider, new Point(HeaderWidth - 0.5, 0), new Point(HeaderWidth - 0.5, height));

        // Заголовки дорожек; ромбы рисует слой бегунка — они зависят от его места
        dc.DrawText(Text("ВИДЕО", 11, tertiary, true), new Point(12, TrackTop + 6));
        if (Clips.Count > Selected)
        {
            var name = Text(Clips[Selected].Name, 12, secondary);
            name.MaxTextWidth = HeaderWidth - 24;
            name.MaxLineCount = 2;
            name.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(name, new Point(12, TrackTop + 22));
        }
        for (var lane = 0; lane < LaneNames.Length; lane++)
        {
            var y = LaneTop + lane * LaneHeight;
            if (lane % 2 == 0)
                dc.DrawRectangle(Res("ControlFill"), null, new Rect(0, y, width, LaneHeight));
            dc.DrawText(Text(LaneNames[lane], 12, secondary), new Point(12, y + (LaneHeight - 16) / 2));
        }

        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, 0, Math.Max(0, width - HeaderWidth), height)));
        DrawRuler(dc, tertiary, secondary);
        DrawClips(dc, text, accent);
        DrawKeys(dc, text, accent);
        dc.Pop();

        // Полоса прокрутки
        var total = Total.TotalSeconds * _pps + 60 + Pad;
        if (total > ContentWidth + 1)
        {
            var track = new Rect(HeaderWidth + 4, height - ScrollHeight + 2, ContentWidth - 8, 5);
            var thumbWidth = Math.Max(30, track.Width * ContentWidth / total);
            var thumbX = track.X + (track.Width - thumbWidth) * (_scroll / Math.Max(1, MaxScroll));
            dc.DrawRoundedRectangle(Alpha(Res("ControlStrongFill"), 0x70), null, new Rect(thumbX, track.Y, thumbWidth, track.Height), 2.5, 2.5);
        }
    }

    private void DrawRuler(DrawingContext dc, Brush tick, Brush label)
    {
        // Шаг подписей — чтобы между ними было не меньше 90 точек
        double[] steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200];
        var step = steps.FirstOrDefault(s => s * _pps >= 90, 1800);
        var minor = step / (step is 2 or 0.2 or 20 ? 4 : 5);
        var pen = new Pen(tick, 1);
        var start = Math.Floor(At(HeaderWidth).TotalSeconds / minor) * minor;
        var end = At(ActualWidth).TotalSeconds;
        for (var t = start; t <= end + minor; t += minor)
        {
            var x = Math.Round(X(TimeSpan.FromSeconds(t))) + 0.5;
            var major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
            dc.DrawLine(pen, new Point(x, RulerHeight - (major ? 10 : 5)), new Point(x, RulerHeight));
            if (major)
                dc.DrawText(Text(Label(TimeSpan.FromSeconds(Math.Round(t, 3)), step), 11, label), new Point(x + 4, 4));
        }
        dc.DrawLine(new Pen(Res("DividerStroke"), 1), new Point(HeaderWidth, RulerHeight + 0.5), new Point(ActualWidth, RulerHeight + 0.5));
    }

    private static string Label(TimeSpan time, double step) => step < 1
        ? time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss\.f" : @"m\:ss\.f")
        : time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");

    private void DrawClips(DrawingContext dc, Brush text, Brush accent)
    {
        var start = TimeSpan.Zero;
        var waveBrush = Alpha(Res("SuccessText"), 0xD0);
        for (var i = 0; i < Clips.Count; i++)
        {
            var clip = Clips[i];
            double x1 = X(start) + 1, x2 = X(start + clip.Length) - 1;
            start += clip.Length;
            if (x2 < HeaderWidth || x1 > ActualWidth)
                continue;
            var box = new Rect(x1, TrackTop, Math.Max(2, x2 - x1), TrackHeight);
            var shape = new RectangleGeometry(box, 6, 6);
            dc.PushClip(shape);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x26, 0x2B, 0x33)), null, box);

            // Кадры: плитки по высоте дорожки без полосы звука, каждая — ближайшая миниатюра к своему времени
            var thumbs = Thumbnails?.Invoke(clip.Path);
            var thumbHeight = TrackHeight - WaveHeight;
            if (thumbs is { Count: > 0 })
            {
                var tile = thumbHeight * thumbs[0].Image.PixelWidth / thumbs[0].Image.PixelHeight;
                var first = Math.Max(x1, HeaderWidth - tile);
                for (var x = x1 + Math.Floor((first - x1) / tile) * tile; x < Math.Min(x2, ActualWidth); x += tile)
                {
                    var time = clip.In + TimeSpan.FromSeconds((x + tile / 2 - x1) / _pps);
                    var nearest = thumbs.MinBy(t => (t.Time - time).Duration()).Image;
                    dc.DrawImage(nearest, new Rect(x, TrackTop, tile, thumbHeight));
                }
            }

            // Звук: волна с учётом громкости по ключам
            var waveTop = TrackTop + thumbHeight;
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x22, 0x1E)), null, new Rect(x1, waveTop, box.Width, WaveHeight));
            if (clip.HasAudio && Waveform?.Invoke(clip.Path) is { Length: > 0 } peaks)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    var middle = waveTop + WaveHeight / 2;
                    var left = Math.Max(x1, HeaderWidth);
                    var right = Math.Min(x2, ActualWidth);
                    g.BeginFigure(new Point(left, middle), true, true);
                    var points = new List<Point>();
                    for (var x = left; x <= right; x += 2)
                    {
                        var time = clip.In + TimeSpan.FromSeconds((x - x1) / _pps);
                        var from = (int)(time.TotalSeconds * WavePerSecond);
                        var to = Math.Max(from + 1, (int)((time.TotalSeconds + 2 / _pps) * WavePerSecond));
                        var peak = 0f;
                        for (var k = Math.Max(0, from); k < Math.Min(peaks.Length, to); k++)
                            peak = Math.Max(peak, peaks[k]);
                        var level = Math.Min(1, Math.Sqrt(peak) * clip.Animation.Volume.At(time).A) * (WaveHeight / 2 - 1);
                        points.Add(new Point(x, middle - level));
                        g.LineTo(new Point(x, middle - level), false, false);
                    }
                    for (var k = points.Count - 1; k >= 0; k--)
                        g.LineTo(new Point(points[k].X, 2 * middle - points[k].Y), false, false);
                }
                geometry.Freeze();
                dc.DrawGeometry(waveBrush, null, geometry);
            }

            // Подпись: имя и длина
            var label = Text($"{clip.Name}  ·  {Format(clip.Length)}", 11, Brushes.White);
            label.MaxTextWidth = Math.Max(1, box.Width - 14);
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            if (box.Width > 30)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), null,
                    new Rect(x1 + 4, TrackTop + 4, Math.Min(label.Width + 10, box.Width - 8), 18), 4, 4);
                dc.DrawText(label, new Point(x1 + 9, TrackTop + 5));
            }
            if (clip.Crop is not null || !clip.Animation.IsDefault)
                dc.DrawText(Text("", 11, Brushes.White, icon: true), new Point(x2 - 18, TrackTop + 7)); // значок: есть анимация или приближение
            dc.Pop();

            var selected = i == Selected;
            dc.DrawRoundedRectangle(null, new Pen(selected ? accent : Alpha(text, 0x30), selected ? 2 : 1), box, 6, 6);
            if (selected)
            {
                // Ручки обрезки
                foreach (var x in new[] { x1 + 3, x2 - 3 })
                {
                    dc.DrawRoundedRectangle(accent, null, new Rect(x - 3, TrackTop + 2, 6, TrackHeight - 4), 3, 3);
                    dc.DrawRectangle(Brushes.White, null, new Rect(x - 0.75, TrackTop + TrackHeight / 2 - 8, 1.5, 16));
                }
            }
        }
    }

    private void DrawKeys(DrawingContext dc, Brush text, Brush accent)
    {
        if (Clips.Count <= Selected)
            return;
        var clip = Clips[Selected];
        var start = ClipStart(Selected);
        double x1 = X(start), x2 = X(start + clip.Length);
        // Отрезок выбранного клипа на дорожках свойств
        dc.DrawRectangle(Alpha(accent, 0x18), null, new Rect(x1, LaneTop, Math.Max(0, x2 - x1), LaneNames.Length * LaneHeight));
        var line = new Pen(Alpha(text, 0x50), 1.5);
        for (var lane = 0; lane < LaneNames.Length; lane++)
        {
            var value = Lane(clip, lane);
            var y = LaneTop + lane * LaneHeight + LaneHeight / 2;
            var visible = value.Keys.Where(k => k.Time >= clip.In && k.Time <= clip.Out).ToList();
            for (var k = 0; k + 1 < visible.Count; k++)
                dc.DrawLine(line, new Point(KeyX(visible[k], clip, start), y), new Point(KeyX(visible[k + 1], clip, start), y));
            foreach (var key in visible)
            {
                var selected = ReferenceEquals(key, SelectedKey);
                DrawKey(dc, new Point(KeyX(key, clip, start), y), key.Ease, selected ? accent : text, selected ? new Pen(Brushes.White, 1.5) : null, 6);
            }
        }
    }

    private double KeyX(Keyframe key, VideoClip clip, TimeSpan clipStart) => X(clipStart + (key.Time - clip.In));

    /// <summary>Ромб — ровный переход, круг — плавный, квадрат — скачком (как значки ключей в After Effects).</summary>
    public static void DrawKey(DrawingContext dc, Point center, KeyEase ease, Brush? fill, Pen? pen, double r)
    {
        switch (ease)
        {
            case KeyEase.Smooth:
                dc.DrawEllipse(fill, pen, center, r * 0.85, r * 0.85);
                break;
            case KeyEase.Hold:
                dc.DrawRectangle(fill, pen, new Rect(center.X - r * 0.75, center.Y - r * 0.75, r * 1.5, r * 1.5));
                break;
            default:
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(center.X, center.Y - r), true, true);
                    g.LineTo(new Point(center.X + r, center.Y), true, true);
                    g.LineTo(new Point(center.X, center.Y + r), true, true);
                    g.LineTo(new Point(center.X - r, center.Y), true, true);
                }
                geometry.Freeze();
                dc.DrawGeometry(fill, pen, geometry);
                break;
        }
    }

    /// <summary>Бегунок и кнопки ключей в заголовках — перерисовываются на каждом кадре воспроизведения отдельно от шкалы.</summary>
    private void DrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        if (ActualWidth <= HeaderWidth)
            return;
        var accent = Res("AccentBrush");
        var text = Res("TextPrimary");
        var tertiary = Res("TextTertiary");

        if (Clips.Count > Selected)
        {
            var clip = Clips[Selected];
            var time = SourceTime(_position);
            for (var lane = 0; lane < LaneNames.Length; lane++)
            {
                var value = Lane(clip, lane);
                var y = LaneTop + lane * LaneHeight + LaneHeight / 2;
                var here = value.KeyAt(time);
                var arrows = value.IsAnimated ? Res("TextSecondary") : Alpha(tertiary, 0x50);
                dc.DrawText(Text("", 10, arrows, icon: true), new Point(HeaderWidth - 56, y - 6));
                dc.DrawText(Text("", 10, arrows, icon: true), new Point(HeaderWidth - 18, y - 6));
                var center = new Point(HeaderWidth - 33, y);
                if (here is not null)
                    DrawKey(dc, center, here.Ease, accent, null, 6);
                else
                    DrawKey(dc, center, KeyEase.Linear, null, new Pen(value.IsAnimated ? accent : tertiary, 1.3), 5.5);
            }
        }

        var x = X(_position);
        if (x < HeaderWidth - 1 || x > ActualWidth + 1)
            return;
        x = Math.Round(x) + 0.5;
        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth - 8, 0, ActualWidth - HeaderWidth + 8, ActualHeight)));
        dc.DrawLine(new Pen(text, 1.5), new Point(x, RulerHeight - 6), new Point(x, ActualHeight - ScrollHeight));
        // Ручка бегунка на линейке
        var handle = new StreamGeometry();
        using (var g = handle.Open())
        {
            g.BeginFigure(new Point(x - 6, 2), true, true);
            g.LineTo(new Point(x + 6, 2), true, true);
            g.LineTo(new Point(x + 6, RulerHeight - 12), true, true);
            g.LineTo(new Point(x, RulerHeight - 5), true, true);
            g.LineTo(new Point(x - 6, RulerHeight - 12), true, true);
        }
        handle.Freeze();
        dc.DrawGeometry(text, null, handle);
        dc.Pop();
    }

    private static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.f") : time.ToString(@"m\:ss\.f");

    // ---------------------------------------------------------------- мышь

    private int ClipAt(double x)
    {
        var time = At(x);
        var start = TimeSpan.Zero;
        for (var i = 0; i < Clips.Count; i++)
        {
            if (time >= start && time < start + Clips[i].Length)
                return i;
            start += Clips[i].Length;
        }
        return -1;
    }

    /// <summary>Край клипа под мышью: (клип, true — левый); вне краёв — (-1, false).</summary>
    private (int Clip, bool Left) EdgeAt(double x)
    {
        var start = TimeSpan.Zero;
        for (var i = 0; i < Clips.Count; i++)
        {
            double x1 = X(start), x2 = X(start + Clips[i].Length);
            if (Math.Abs(x - x1) <= Edge && x >= x1 - 2)
                return (i, true);
            if (Math.Abs(x - x2) <= Edge && x <= x2 + 2)
                return (i, false);
            start += Clips[i].Length;
        }
        return (-1, false);
    }

    private int LaneAt(double y) => y >= LaneTop && y < LaneTop + LaneNames.Length * LaneHeight ? (int)((y - LaneTop) / LaneHeight) : -1;

    private Keyframe? KeyHit(int lane, double x)
    {
        if (lane < 0 || Clips.Count <= Selected)
            return null;
        var clip = Clips[Selected];
        var start = ClipStart(Selected);
        return Lane(clip, lane).Keys.Where(k => k.Time >= clip.In && k.Time <= clip.Out)
            .Select(k => (Key: k, Distance: Math.Abs(KeyX(k, clip, start) - x)))
            .Where(h => h.Distance <= 7).OrderBy(h => h.Distance).Select(h => h.Key).FirstOrDefault();
    }

    private void StartEdit()
    {
        if (_changed)
            return;
        _changed = true;
        BeginEdit?.Invoke();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        e.Handled = true;
        _changed = false;
        _downX = p.X;
        var lane = LaneAt(p.Y);
        if (p.X < HeaderWidth)
        {
            if (lane >= 0 && p.X >= HeaderWidth - 62)
                KeyAction?.Invoke(lane, p.X < HeaderWidth - 44 ? KeyCommand.Previous : p.X < HeaderWidth - 22 ? KeyCommand.Toggle : KeyCommand.Next);
            return;
        }
        CaptureMouse();
        if (p.Y >= ActualHeight - ScrollHeight - 2)
        {
            _drag = Drag.Scroll;
            ScrollTo(p.X);
            return;
        }
        if (p.Y >= TrackTop && p.Y < TrackTop + TrackHeight && Clips.Count > 0)
        {
            var (edge, left) = EdgeAt(p.X);
            if (edge >= 0)
            {
                _drag = left ? Drag.TrimIn : Drag.TrimOut;
                _dragClip = edge;
                _origin = left ? Clips[edge].In : Clips[edge].Out;
                return;
            }
            var index = ClipAt(p.X);
            if (index >= 0)
            {
                _drag = Drag.Clip;
                _dragClip = index;
                _grabOffset = At(p.X) - ClipStart(index);
                Seek?.Invoke(At(p.X));
                return;
            }
        }
        if (lane >= 0 && KeyHit(lane, p.X) is { } key)
        {
            if (e.ClickCount == 2)
                return;
            _drag = Drag.Key;
            _dragValue = Lane(Clips[Selected], lane);
            SelectedKey = key;
            _origin = key.Time;
            Seek?.Invoke(ClipStart(Selected) + (key.Time - Clips[Selected].In));
            InvalidateVisual();
            return;
        }
        if (lane >= 0 && e.ClickCount == 2)
        {
            ReleaseMouseCapture();
            Seek?.Invoke(Clamp(At(p.X), TimeSpan.Zero, Total));
            KeyAction?.Invoke(lane, KeyCommand.Add);
            return;
        }
        if (SelectedKey is not null)
        {
            SelectedKey = null;
            InvalidateVisual();
        }
        _drag = Drag.Playhead;
        Seek?.Invoke(Clamp(At(p.X), TimeSpan.Zero, Total));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            var onTrack = p.Y >= TrackTop && p.Y < TrackTop + TrackHeight;
            Cursor = p.X < HeaderWidth ? Cursors.Arrow
                : onTrack && EdgeAt(p.X).Clip >= 0 ? Cursors.SizeWE
                : KeyHit(LaneAt(p.Y), p.X) is not null ? Cursors.Hand
                : Cursors.Arrow;
            return;
        }
        var delta = TimeSpan.FromSeconds((p.X - _downX) / _pps);
        switch (_drag)
        {
            case Drag.Scroll:
                ScrollTo(p.X);
                break;
            case Drag.Playhead:
                AutoScroll(p.X);
                Seek?.Invoke(Clamp(At(p.X), TimeSpan.Zero, Total));
                break;
            case Drag.TrimIn:
            {
                var clip = Clips[_dragClip];
                StartEdit();
                clip.In = Clamp(_origin + delta, TimeSpan.Zero, clip.Out - MinLength);
                Edited?.Invoke();
                Seek?.Invoke(ClipStart(_dragClip));
                Refresh();
                break;
            }
            case Drag.TrimOut:
            {
                var clip = Clips[_dragClip];
                StartEdit();
                clip.Out = Clamp(_origin + delta, clip.In + MinLength, clip.Duration);
                Edited?.Invoke();
                Seek?.Invoke(ClipStart(_dragClip) + clip.Length - TimeSpan.FromMilliseconds(1));
                Refresh();
                break;
            }
            case Drag.Clip when Math.Abs(p.X - _downX) > 6 && Clips.Count > 1:
            {
                // Клип «едет» за мышью: переставляем, когда его середина заходит за середину соседа
                var middle = At(p.X) - _grabOffset + Clips[_dragClip].Length / 2;
                var target = _dragClip;
                if (target > 0 && middle < ClipStart(target - 1) + Clips[target - 1].Length / 2)
                    target--;
                else if (target < Clips.Count - 1 && middle > ClipStart(target + 1) + Clips[target + 1].Length / 2)
                    target++;
                if (target != _dragClip && Clips is List<VideoClip> list)
                {
                    StartEdit();
                    (list[_dragClip], list[target]) = (list[target], list[_dragClip]);
                    _dragClip = target;
                    Edited?.Invoke();
                    Seek?.Invoke(ClipStart(target) + _grabOffset);
                    Refresh();
                }
                break;
            }
            case Drag.Key when _dragValue is not null && SelectedKey is { } key:
            {
                var clip = Clips[Selected];
                var start = ClipStart(Selected);
                var time = Clamp(_origin + delta, clip.In, clip.Out);
                // Прилипание к ключам других свойств — удобно выровнять
                foreach (var other in clip.Animation.All.Where(v => v != _dragValue).SelectMany(v => v.Keys))
                    if (Math.Abs((other.Time - time).TotalSeconds * _pps) < 5)
                        time = other.Time;
                if (time == key.Time)
                    break;
                StartEdit();
                key.Time = time;
                _dragValue.Sort();
                Edited?.Invoke();
                Seek?.Invoke(start + (time - clip.In));
                InvalidateVisual();
                break;
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _drag = Drag.None;
        _dragValue = null;
        Refresh();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        e.Handled = true;
        if (p.X < HeaderWidth)
            return;
        var lane = LaneAt(p.Y);
        if (lane >= 0 && KeyHit(lane, p.X) is { } key)
        {
            var value = Lane(Clips[Selected], lane);
            SelectedKey = key;
            InvalidateVisual();
            var menu = new ContextMenu();
            foreach (var (ease, title) in new[] { (KeyEase.Linear, "Ровно"), (KeyEase.Smooth, "Плавно"), (KeyEase.Hold, "Скачком") })
            {
                var item = new MenuItem { Header = $"Переход: {title}", IsCheckable = true, IsChecked = key.Ease == ease };
                item.Click += (_, _) =>
                {
                    BeginEdit?.Invoke();
                    key.Ease = ease;
                    Edited?.Invoke();
                    Refresh();
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var remove = new MenuItem { Header = "Удалить ключ" };
            remove.Click += (_, _) =>
            {
                BeginEdit?.Invoke();
                value.Remove(key);
                SelectedKey = null;
                Edited?.Invoke();
                Refresh();
            };
            menu.Items.Add(remove);
            menu.IsOpen = true;
            return;
        }
        if (p.Y >= TrackTop && p.Y < TrackTop + TrackHeight && ClipAt(p.X) is var index and >= 0)
        {
            Seek?.Invoke(At(p.X));
            var menu = new ContextMenu();
            var split = new MenuItem { Header = "Разрезать здесь", InputGestureText = "S" };
            split.Click += (_, _) => SplitRequested?.Invoke(index);
            var reset = new MenuItem { Header = "Сбросить ключи и приближение", IsEnabled = Clips[index].Crop is not null || !Clips[index].Animation.IsDefault };
            reset.Click += (_, _) => ResetRequested?.Invoke(index);
            var delete = new MenuItem { Header = "Удалить клип", InputGestureText = "Delete", IsEnabled = Clips.Count > 1 };
            delete.Click += (_, _) => DeleteRequested?.Invoke(index);
            menu.Items.Add(split);
            menu.Items.Add(reset);
            menu.Items.Add(delete);
            menu.IsOpen = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ZoomAround(Math.Max(HeaderWidth, e.GetPosition(this).X), e.Delta > 0 ? 1.25 : 0.8);
        else
        {
            _scroll = Math.Clamp(_scroll - e.Delta * 0.6, 0, MaxScroll);
            Refresh();
        }
    }

    private void ScrollTo(double x)
    {
        var fraction = (x - HeaderWidth - 4) / Math.Max(1, ContentWidth - 8);
        _scroll = Math.Clamp(fraction * MaxScroll, 0, MaxScroll);
        Refresh();
    }

    private void AutoScroll(double x)
    {
        if (x > ActualWidth - 20)
            _scroll = Math.Min(MaxScroll, _scroll + 12);
        else if (x < HeaderWidth + 10)
            _scroll = Math.Max(0, _scroll - 12);
        else
            return;
        InvalidateVisual();
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;
}
