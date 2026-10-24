using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Orbit.Core;
using Orbit.Shell;
using Path = System.Windows.Shapes.Path;

namespace Orbit.UI;

internal enum WheelMode
{
    /// <summary>Открыто во время перетаскивания: файл отпускают на секторе. Фокус не забирает.</summary>
    Drag,

    /// <summary>Открыто горячей клавишей или из меню: сектор выбирают щелчком или клавиатурой.</summary>
    Click,
}

/// <summary>Круговое меню. Новый экземпляр на каждое появление.</summary>
internal sealed class WheelWindow : Window
{
    public delegate IReadOnlyList<Operation> OperationSource(WheelKind wheel, IReadOnlyList<FileItem> items);

    private const double Outer = 192;
    private const double Inner = 74;
    private const double Disc = Outer + 11;
    private const double Pad = 28;
    private const double Size = 2 * (Disc + Pad);
    private const double Center = Size / 2;
    private const double Gap = 3;
    /// <summary>Толщина обводки цветом сектора — она и скругляет углы.</summary>
    private const double Rounding = 8;
    private const double PopOut = 6;
    private const int PageSize = 10;

    private static readonly string[] WheelNames = ["Конвертация", "Инструменты", "Действия"];
    private static readonly string[] WheelKeys = ["Shift", "Ctrl", "Alt"];

    private readonly OperationSource _source;
    private readonly Action<Operation, IReadOnlyList<FileItem>> _execute;
    private readonly Canvas _canvas = new() { Width = Size, Height = Size };
    private readonly Canvas _sliceLayer = new() { Width = Size, Height = Size, IsHitTestVisible = false };
    private readonly List<Slice> _slices = [];
    private readonly StackPanel _hub = new() { Width = (Inner - 6) * 1.6, IsHitTestVisible = false };
    private readonly Image _thumb = new() { Width = 40, Height = 40, Margin = new Thickness(0, 0, 0, 4), Stretch = Stretch.Uniform };
    private readonly TextBlock _hubGlyph = new() { FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _title = HubText(14, FontWeights.SemiBold);
    private readonly TextBlock _detail = HubText(11.5, FontWeights.Normal);
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ScaleTransform _zoom = new(0.86, 0.86);

    private IReadOnlyList<FileItem> _items;
    private IReadOnlyList<Operation> _ops = [];
    private WheelKind _wheel;
    private Native.POINT _anchor;
    private int _page;
    private int _highlight = -1;
    private bool _finished;
    private bool _dropped;
    private DispatcherTimer? _flipTimer;
    private DispatcherTimer? _closeTimer;

    private sealed class Slice
    {
        public required Canvas Group { get; init; }
        public required Path Shape { get; init; }
        public required TextBlock[] Texts { get; init; }
        public required TranslateTransform Offset { get; init; }
        public required double MidAngle { get; init; }
        public Operation? Op { get; init; }
        public bool IsMore => Op is null;
    }

    public WheelWindow(WheelMode mode, WheelKind wheel, IReadOnlyList<FileItem>? items, OperationSource source, Action<Operation, IReadOnlyList<FileItem>> execute)
    {
        Mode = mode;
        _wheel = wheel;
        _items = items ?? [];
        _source = source;
        _execute = execute;

        Title = "Orbit";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = mode == WheelMode.Click;
        AllowDrop = true;
        Width = Size;
        Height = Size;
        Left = -32000;
        Top = -32000;
        Opacity = 0;
        Focusable = true;
        SetResourceReference(FontFamilyProperty, "UiFont");
        Content = _canvas;

        // Подложка: она же делает окно «непрозрачным» для мыши и перетаскивания по всему кругу.
        var disc = new Ellipse { Width = Disc * 2, Height = Disc * 2, StrokeThickness = 1 };
        disc.SetResourceReference(Shape.FillProperty, "WheelFill");
        disc.SetResourceReference(Shape.StrokeProperty, "WheelStroke");
        disc.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.3 };
        Canvas.SetLeft(disc, Center - Disc);
        Canvas.SetTop(disc, Center - Disc);
        _canvas.Children.Add(disc);
        _canvas.Children.Add(_sliceLayer);

        var hubDisc = new Ellipse { Width = (Inner - 6) * 2, Height = (Inner - 6) * 2, StrokeThickness = 1, IsHitTestVisible = false };
        hubDisc.SetResourceReference(Shape.FillProperty, "WheelHubFill");
        hubDisc.SetResourceReference(Shape.StrokeProperty, "WheelStroke");
        Canvas.SetLeft(hubDisc, Center - Inner + 6);
        Canvas.SetTop(hubDisc, Center - Inner + 6);
        _canvas.Children.Add(hubDisc);

        _hubGlyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _hubGlyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        _hub.Children.Add(_thumb);
        _hub.Children.Add(_hubGlyph);
        _hub.Children.Add(_title);
        _hub.Children.Add(_detail);
        _hub.Children.Add(_dots);
        _canvas.Children.Add(_hub);

        _canvas.RenderTransformOrigin = new Point(0.5, 0.5);
        _canvas.RenderTransform = _zoom;

        SourceInitialized += (_, _) =>
            Native.AddExStyle(Handle, Native.WS_EX_TOOLWINDOW | (mode == WheelMode.Drag ? Native.WS_EX_NOACTIVATE : 0));
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => Native.PlaceWindow(Handle, _anchor, centered: true));

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, e) =>
        {
            SetHighlight(-1);
            e.Handled = true;
        };
        Drop += OnDrop;
        MouseMove += (s, e) =>
        {
            if (Mode == WheelMode.Click)
                SetHighlight(IndexAt(e.GetPosition(_canvas), out _));
        };
        MouseLeftButtonUp += OnClick;
        MouseRightButtonUp += (_, _) => Finish();
        MouseWheel += (_, e) => FlipPage(e.Delta < 0 ? 1 : -1);
        KeyDown += OnKeyDown;
        Deactivated += (_, _) =>
        {
            if (Mode == WheelMode.Click)
                Finish();
        };
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;

        if (_items.Count > 0)
            _ops = _source(_wheel, _items);
        Build(animate: false);
    }

    public WheelMode Mode { get; private set; }

    public WheelKind CurrentWheel => _wheel;

    public bool IsFinished => _finished;

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    private int PageCount => _ops.Count <= PageSize ? 1 : (int)Math.Ceiling(_ops.Count / (double)(PageSize - 1));

    // ------------------------------------------------------------ показ и закрытие

    public void ShowAt(Native.POINT screenPoint)
    {
        _anchor = screenPoint;
        Show();
        Native.PlaceWindow(Handle, _anchor, centered: true);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(170);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, duration) { EasingFunction = ease });
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, duration) { EasingFunction = ease });
        if (Mode == WheelMode.Click)
            Focus();
    }

    /// <summary>True, если экранная точка (физические пиксели) лежит на круге.</summary>
    public bool Contains(Native.POINT p)
    {
        if (!Native.GetWindowRect(Handle, out var rect))
            return false;
        var scale = (rect.Right - rect.Left) / Size;
        var cx = (rect.Left + rect.Right) / 2.0;
        var cy = (rect.Top + rect.Bottom) / 2.0;
        var radius = Disc * scale;
        return Math.Pow(p.X - cx, 2) + Math.Pow(p.Y - cy, 2) <= radius * radius;
    }

    public void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        _flipTimer?.Stop();
        _closeTimer?.Stop();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(110)));
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(110)));
    }

    /// <summary>Отпустили кнопку мыши где угодно: если файл так и не бросили на круг — закрываемся.</summary>
    public void OnDragEnded()
    {
        if (Mode != WheelMode.Drag || _finished)
            return;
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            if (Mode == WheelMode.Drag && !_dropped)
                Finish();
        };
        _closeTimer.Start();
    }

    /// <summary>Повторное нажатие той же клавиши листает страницы, другая клавиша — переключает колесо.</summary>
    public void SwitchTo(WheelKind wheel)
    {
        if (wheel == _wheel)
        {
            FlipPage(1);
            return;
        }
        _wheel = wheel;
        _page = 0;
        _ops = _items.Count > 0 ? _source(_wheel, _items) : [];
        Build(animate: true);
    }

    private void EnterClickMode()
    {
        Mode = WheelMode.Click;
        var hwnd = Handle;
        var style = Native.WS_EX_TOOLWINDOW;
        Native.AddExStyle(hwnd, style);
        RemoveNoActivate(hwnd);
        Native.ForceForeground(hwnd);
        Activate();
        Focus();
        SetHighlight(-1);
        UpdateHub();
    }

    private static void RemoveNoActivate(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() & ~Native.WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(style));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    // ------------------------------------------------------------ построение секторов

    private IReadOnlyList<Operation?> VisibleSlices()
    {
        if (PageCount == 1)
            return _ops;
        var perPage = PageSize - 1;
        return [.. _ops.Skip(_page * perPage).Take(perPage), null];
    }

    private void Build(bool animate)
    {
        _sliceLayer.Children.Clear();
        _slices.Clear();
        _highlight = -1;
        var visible = VisibleSlices();
        var n = visible.Count;
        var span = 360.0 / Math.Max(1, n);
        var mid = (Inner + Outer) / 2;
        var labelWidth = n <= 1 ? 120 : Math.Min(104, 2 * mid * Math.Sin(span / 2 * Math.PI / 180) - 10);

        for (var i = 0; i < n; i++)
        {
            var op = visible[i];
            var midAngle = -90 + i * span;
            var shape = new Path
            {
                Data = n == 1 ? Ring() : SliceGeometry(midAngle - span / 2, midAngle + span / 2),
                StrokeThickness = n == 1 ? 0 : Rounding,
                StrokeLineJoin = PenLineJoin.Round,
            };
            shape.SetResourceReference(Shape.FillProperty, "SliceFill");
            shape.SetResourceReference(Shape.StrokeProperty, "SliceFill");

            var glyph = new TextBlock
            {
                Text = op?.Glyph ?? Glyphs.More,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 3),
            };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            var label = new TextBlock
            {
                Text = op?.Label ?? "Ещё",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 34,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var sub = new TextBlock
            {
                Text = op is null ? $"{_page + 1} из {PageCount}" : op.Subtitle ?? "",
                FontSize = 10.5,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = op is null || op.Subtitle is not null ? Visibility.Visible : Visibility.Collapsed,
            };
            var stack = new StackPanel { Width = labelWidth };
            stack.Children.Add(glyph);
            stack.Children.Add(label);
            stack.Children.Add(sub);
            stack.Measure(new Size(labelWidth, double.PositiveInfinity));
            var rad = midAngle * Math.PI / 180;
            Canvas.SetLeft(stack, Center + mid * Math.Cos(rad) - labelWidth / 2);
            Canvas.SetTop(stack, Center + mid * Math.Sin(rad) - stack.DesiredSize.Height / 2);

            var offset = new TranslateTransform();
            var scale = new ScaleTransform(1, 1);
            var group = new Canvas
            {
                Width = Size,
                Height = Size,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { scale, offset } },
            };
            group.Children.Add(shape);
            group.Children.Add(stack);
            _sliceLayer.Children.Add(group);

            var slice = new Slice { Group = group, Shape = shape, Texts = [glyph, label, sub], Offset = offset, MidAngle = midAngle, Op = op };
            _slices.Add(slice);
            Paint(slice, highlighted: false);

            if (animate)
            {
                var delay = TimeSpan.FromMilliseconds(14 * i);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                group.Opacity = 0;
                group.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { BeginTime = delay });
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = delay, EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = delay, EasingFunction = ease });
            }
        }
        BuildDots();
        UpdateHub();
    }

    /// <summary>Сектор кольца с зазором постоянной ширины; углы скругляет обводка толщиной <see cref="Rounding"/>.</summary>
    private static Geometry SliceGeometry(double fromDeg, double toDeg)
    {
        var inset = Rounding / 2;
        var rOut = Outer - inset;
        var rIn = Inner + inset;
        var halfGap = Gap / 2 + inset;
        var a0 = fromDeg * Math.PI / 180;
        var a1 = toDeg * Math.PI / 180;
        var gOut = Math.Asin(halfGap / rOut);
        var gIn = Math.Asin(halfGap / rIn);
        var large = a1 - a0 - 2 * gOut > Math.PI;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Polar(rOut, a0 + gOut), isFilled: true, isClosed: true);
            ctx.ArcTo(Polar(rOut, a1 - gOut), new Size(rOut, rOut), 0, large, SweepDirection.Clockwise, true, true);
            ctx.LineTo(Polar(rIn, a1 - gIn), true, true);
            ctx.ArcTo(Polar(rIn, a0 + gIn), new Size(rIn, rIn), 0, large, SweepDirection.Counterclockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Ring()
    {
        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new EllipseGeometry(new Point(Center, Center), Outer, Outer));
        geometry.Children.Add(new EllipseGeometry(new Point(Center, Center), Inner, Inner));
        geometry.Freeze();
        return geometry;
    }

    private static Point Polar(double r, double angle) => new(Center + r * Math.Cos(angle), Center + r * Math.Sin(angle));

    private void Paint(Slice slice, bool highlighted)
    {
        var fill = highlighted ? "AccentBrush" : "SliceFill";
        slice.Shape.SetResourceReference(Shape.FillProperty, fill);
        slice.Shape.SetResourceReference(Shape.StrokeProperty, fill);
        slice.Texts[0].SetResourceReference(TextBlock.ForegroundProperty, highlighted ? "TextOnAccent" : "AccentTextBrush");
        slice.Texts[1].SetResourceReference(TextBlock.ForegroundProperty, highlighted ? "TextOnAccent" : "TextPrimary");
        slice.Texts[2].SetResourceReference(TextBlock.ForegroundProperty, highlighted ? "TextOnAccent" : "TextSecondary");
        slice.Texts[2].Opacity = highlighted ? 0.85 : 1;
    }

    private void BuildDots()
    {
        _dots.Children.Clear();
        foreach (WheelKind kind in Enum.GetValues<WheelKind>())
        {
            var dot = new Border
            {
                Height = 6,
                Width = kind == _wheel ? 16 : 6,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(2.5, 0, 2.5, 0),
            };
            dot.SetResourceReference(Border.BackgroundProperty, kind == _wheel ? "AccentBrush" : "ControlStrongStroke");
            _dots.Children.Add(dot);
        }
    }

    // ------------------------------------------------------------ центр круга

    private static TextBlock HubText(double size, FontWeight weight) => new()
    {
        FontSize = size,
        FontWeight = weight,
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        HorizontalAlignment = HorizontalAlignment.Center,
        MaxWidth = (Inner - 6) * 1.6,
    };

    private void UpdateHub()
    {
        var k = (int)_wheel;
        var slice = _highlight >= 0 && _highlight < _slices.Count ? _slices[_highlight] : null;
        string title, detail;
        if (slice?.Op is { } op)
        {
            title = op.Label;
            detail = op.Subtitle ?? WheelNames[k];
        }
        else if (slice is not null)
        {
            title = "Ещё";
            detail = Mode == WheelMode.Drag ? "задержите, чтобы листать" : $"страница {_page + 1} из {PageCount}";
        }
        else if (_items.Count == 0)
        {
            title = WheelNames[k];
            detail = "Отпустите файл на действии";
        }
        else if (_ops.Count == 0)
        {
            title = "Нет действий";
            detail = _wheel == WheelKind.Convert ? "Попробуйте Ctrl или Alt" : "Попробуйте другое колесо";
        }
        else
        {
            title = _items.Count == 1 ? _items[0].Name : $"{_items.Count} {Job.Plural(_items.Count, "файл", "файла", "файлов")}";
            detail = $"{WheelNames[k]} · {WheelKeys[k]}";
        }
        _title.Text = title;
        _detail.Text = detail;

        // Пока ничего не выбрано — миниатюра файла, при наведении — значок действия.
        var thumbnail = slice is null && _items.Count > 0 ? Thumbnail() : null;
        _thumb.Source = thumbnail;
        _thumb.Visibility = thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
        _hubGlyph.Text = slice?.Op?.Glyph ?? (slice is not null ? Glyphs.More : WheelGlyph());
        _hubGlyph.Visibility = thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

        _hub.Measure(new Size(_hub.Width, double.PositiveInfinity));
        Canvas.SetLeft(_hub, Center - _hub.Width / 2);
        Canvas.SetTop(_hub, Center - _hub.DesiredSize.Height / 2);
    }

    private string WheelGlyph() => _wheel switch
    {
        WheelKind.Convert => "",
        WheelKind.Tools => "",
        _ => "",
    };

    private ImageSource? _thumbnailCache;
    private bool _thumbnailLoaded;

    private ImageSource? Thumbnail()
    {
        if (!_thumbnailLoaded)
        {
            _thumbnailLoaded = true;
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _thumbnailCache = ShellThumbnail.Get(_items[0].Path, (int)Math.Round(40 * dpi));
        }
        return _thumbnailCache;
    }

    // ------------------------------------------------------------ выбор сектора

    private int IndexAt(Point p, out bool onHub)
    {
        var dx = p.X - Center;
        var dy = p.Y - Center;
        var r = Math.Sqrt(dx * dx + dy * dy);
        onHub = r < Inner - 2;
        if (onHub || r > Disc + 6 || _slices.Count == 0)
            return -1;
        var fromTop = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;
        var span = 360.0 / _slices.Count;
        return (int)((fromTop + span / 2) % 360 / span) % _slices.Count;
    }

    private void SetHighlight(int index)
    {
        if (index == _highlight)
            return;
        if (_highlight >= 0 && _highlight < _slices.Count)
        {
            var old = _slices[_highlight];
            Paint(old, highlighted: false);
            Move(old, 0);
        }
        _highlight = index;
        if (index >= 0)
        {
            var slice = _slices[index];
            Paint(slice, highlighted: true);
            if (_slices.Count > 1)
                Move(slice, PopOut);
        }
        ArmFlip();
        UpdateHub();
    }

    private static void Move(Slice slice, double distance)
    {
        var rad = slice.MidAngle * Math.PI / 180;
        var duration = TimeSpan.FromMilliseconds(120);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        slice.Offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(distance * Math.Cos(rad), duration) { EasingFunction = ease });
        slice.Offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance * Math.Sin(rad), duration) { EasingFunction = ease });
    }

    /// <summary>При перетаскивании «Ещё» листает страницы, если задержать файл над ним.</summary>
    private void ArmFlip()
    {
        _flipTimer?.Stop();
        if (Mode != WheelMode.Drag || _highlight < 0 || !_slices[_highlight].IsMore)
            return;
        _flipTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _flipTimer.Tick -= OnFlipTick;
        _flipTimer.Tick += OnFlipTick;
        _flipTimer.Start();
    }

    private void OnFlipTick(object? sender, EventArgs e)
    {
        _flipTimer?.Stop();
        FlipPage(1);
    }

    private void FlipPage(int delta)
    {
        if (PageCount <= 1 || _finished)
            return;
        _page = (_page + delta + PageCount) % PageCount;
        Build(animate: true);
    }

    private void RunSlice(int index)
    {
        if (index < 0 || index >= _slices.Count)
            return;
        if (_slices[index].Op is { } op)
        {
            Finish();
            _execute(op, _items);
        }
        else
        {
            FlipPage(1);
        }
    }

    // ------------------------------------------------------------ перетаскивание

    private static IReadOnlyList<string> ReadPaths(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths)
                return paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }
        return [];
    }

    /// <summary>Только копирование или ссылка — «перемещение» заставило бы Проводник удалить исходный файл.</summary>
    private static DragDropEffects SafeEffect(DragDropEffects allowed) =>
        (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
        : (allowed & DragDropEffects.Link) != 0 ? DragDropEffects.Link
        : DragDropEffects.None;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_finished)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        if (_items.Count == 0 && ReadPaths(e.Data) is { Count: > 0 } paths)
        {
            _items = paths.Select(p => new FileItem(p)).ToList();
            _ops = _source(_wheel, _items);
            _page = 0;
            Build(animate: true);
        }
        var index = IndexAt(e.GetPosition(_canvas), out var onHub);
        SetHighlight(index);
        e.Effects = _items.Count > 0 && (index >= 0 || onHub) ? SafeEffect(e.AllowedEffects) : DragDropEffects.None;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_finished)
            return;
        _dropped = true;
        if (_items.Count == 0 && ReadPaths(e.Data) is { Count: > 0 } paths)
        {
            _items = paths.Select(p => new FileItem(p)).ToList();
            _ops = _source(_wheel, _items);
            Build(animate: false);
        }
        var index = IndexAt(e.GetPosition(_canvas), out _);
        e.Effects = _items.Count > 0 ? SafeEffect(e.AllowedEffects) : DragDropEffects.None;
        if (index >= 0 && _slices[index].Op is not null)
        {
            RunSlice(index);
            return;
        }
        if (_items.Count == 0)
        {
            Finish();
            return;
        }
        // Бросили в центр или на «Ещё» — круг остаётся открытым, дальше выбирают щелчком.
        if (index >= 0)
            FlipPage(1);
        Dispatcher.BeginInvoke(EnterClickMode, DispatcherPriority.Input);
    }

    // ------------------------------------------------------------ мышь и клавиатура

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (Mode != WheelMode.Click)
            return;
        var index = IndexAt(e.GetPosition(_canvas), out var onHub);
        if (onHub)
            SwitchTo((WheelKind)(((int)_wheel + 1) % 3));
        else if (index >= 0)
            RunSlice(index);
        else if (!Contains(Native.CursorPosition()))
            Finish();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var n = _slices.Count;
        switch (e.Key)
        {
            case Key.Escape:
                Finish();
                break;
            case Key.Right or Key.Down when n > 0:
                SetHighlight((_highlight + 1 + n) % n);
                break;
            case Key.Left or Key.Up when n > 0:
                SetHighlight(_highlight <= 0 ? n - 1 : _highlight - 1);
                break;
            case Key.Enter or Key.Space:
                RunSlice(_highlight);
                break;
            case Key.Tab:
                var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 2 : 1;
                SwitchTo((WheelKind)(((int)_wheel + step) % 3));
                break;
            case Key.PageDown:
                FlipPage(1);
                break;
            case Key.PageUp:
                FlipPage(-1);
                break;
            case >= Key.D1 and <= Key.D9:
                RunSlice(e.Key - Key.D1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnThemeChanged() => BuildDots();

    /// <summary>Для отрисовки превью (режим --render): без анимации появления, с выбранным сектором.</summary>
    internal void PrepareForPreview(int highlight)
    {
        _zoom.ScaleX = 1;
        _zoom.ScaleY = 1;
        SetHighlight(highlight);
    }
}
