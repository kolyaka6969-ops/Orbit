using System.Runtime.InteropServices;
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
/// Редактор изображения из круга инструментов: обрезка рамкой, кругом и пером, кисть, маркер, стрелки и фигуры,
/// текст, мозаика, добавление другого изображения сбоку или поверх, поворот. Каждая правка сразу впечатывается
/// в картинку, поэтому отмена — просто возврат к прошлой картинке. Исходный файл не меняется: сохраняется копия «… (изменено)».
/// </summary>
public partial class ImageEditorWindow : Window
{
    private enum Tool
    {
        Crop,
        Pen,
        Marker,
        Arrow,
        Line,
        Rectangle,
        Ellipse,
        Text,
        Pixelate,
    }

    private enum CropMode
    {
        Rectangle,
        Circle,
        Lasso,
    }

    private enum OverlayDrag
    {
        None,
        Move,
        Resize,
    }

    /// <summary>Сколько памяти отдаём под отмену: снимок 4K — 33 МБ, но хотя бы три шага отмены есть всегда.</summary>
    private const long UndoBudget = 768L * 1024 * 1024;

    private static readonly string ImageFilter =
        "Изображения|" + string.Join(";", Formats.Images.Concat(Formats.Raw).Select(e => "*." + e)) + "|Все файлы|*.*";

    private readonly FileItem _source;
    private readonly string _outputDir;
    private readonly string _format;
    private readonly int _quality;
    private readonly TaskCompletionSource<IReadOnlyList<string>> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<BitmapSource> _undo = [];
    private readonly List<BitmapSource> _redo = [];
    private BitmapSource _image;
    private Tool _tool = Tool.Pen;
    private CropMode _cropMode = CropMode.Rectangle;
    private Color _color = Color.FromRgb(0xE5, 0x39, 0x35);
    private double _strokeSize = 4;
    private double _textSize = 32;
    private bool _dragging;
    private Point _dragStart;
    private Annotation? _drawing;
    private List<Point>? _lasso;
    private OverlayAnnotation? _overlay;
    private OverlayDrag _overlayDrag;
    private Rect _overlayStart;
    private TextBox? _textBox;
    private Point _textAt;
    /// <summary>После обрезки кругом или пером углы прозрачные — JPG и BMP их не хранят, сохраняем в PNG.</summary>
    private bool _needsAlpha;
    private bool _dirty;
    private bool _finished;

    private ImageEditorWindow(BitmapSource image, FileItem source, string outputDir, string format, int quality)
    {
        InitializeComponent();
        ThemeManager.UseMica(this);
        _image = image;
        _source = source;
        _outputDir = outputDir;
        _format = format;
        _quality = quality;
        Title = $"{source.Name} — редактор · Orbit";
        Surface.Image = image;
        PenTool.IsChecked = true;
        UpdateState();
        PreviewKeyDown += OnKey;
        Drop += OnDrop;
        Closing += OnClosing;
        Closed += (_, _) => _result.TrySetCanceled();
    }

    /// <summary>
    /// Открывает редактор и ждёт, пока его закроют. Несколько картинок сразу ставятся в ряд.
    /// Результат — сохранённый файл; закрыли без сохранения — отмена.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> EditAsync(IReadOnlyList<FileItem> items, JobContext context)
    {
        var pictures = await Task.Run(() => items.Select(i => BitmapOps.ReadPng(i.Path)).ToList(), context.Token);
        var window = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var image = BitmapOps.Decode(pictures[0]);
            foreach (var other in pictures.Skip(1))
                image = BitmapOps.Merge(image, BitmapOps.Decode(other), Side.Right);
            var first = items[0];
            var editor = new ImageEditorWindow(image, first, context.OutputDirFor(first), ImageBackend.EditFormatFor(first), context.Settings.ImageQuality);
            editor.Show();
            Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(editor).Handle);
            editor.Activate();
            return editor;
        });
        return await window._result.Task;
    }

    /// <summary>
    /// Для «--render»: редактор после нескольких правок (добавленное сбоку изображение, стрелка, рамка, надпись, мозаика).
    /// В тёмной теме — с выделением обрезки кругом, в светлой — с картинкой поверх, которую ещё двигают.
    /// </summary>
    internal static ImageEditorWindow ForPreview(FileItem item, bool dark)
    {
        var image = BitmapOps.Decode(BitmapOps.ReadPng(item.Path));
        var editor = new ImageEditorWindow(image, item, item.Folder, "png", 90);
        editor.Apply(BitmapOps.Merge(image, BitmapOps.Flip(image), Side.Right));
        double w = editor._image.PixelWidth, h = editor._image.PixelHeight, unit = Math.Max(w, h) / 900;
        var red = Color.FromRgb(0xE5, 0x39, 0x35);
        editor.Apply(BitmapOps.Bake(editor._image, new ShapeAnnotation(ShapeKind.Arrow, red, 5 * unit, new Point(w * 0.08, h * 0.85)) { End = new Point(w * 0.3, h * 0.5) }));
        editor.Apply(BitmapOps.Bake(editor._image, new ShapeAnnotation(ShapeKind.Rectangle, Color.FromRgb(0xFD, 0xD8, 0x35), 4 * unit, new Point(w * 0.3, h * 0.3)) { End = new Point(w * 0.48, h * 0.62) }));
        editor.Apply(BitmapOps.Bake(editor._image, new TextAnnotation("Вот здесь", new Point(w * 0.05, h * 0.06), 36 * unit, red)));
        editor.Apply(BitmapOps.Pixelate(editor._image, new Int32Rect((int)(w * 0.6), (int)(h * 0.2), (int)(w * 0.25), (int)(h * 0.3))));
        if (dark)
        {
            editor.CropTool.IsChecked = true;
            editor.CropCircle.IsChecked = true;
            editor.Surface.DimOutside = true;
            var side = h * 0.8;
            editor.Surface.Selection = new EllipseGeometry(new Rect(w * 0.12, h * 0.1, side, side));
            editor.ShowApplyBar("Обрезать");
            editor.ShowCropSize();
        }
        else
        {
            editor.BeginOverlay(image, scale: unit);
        }
        editor.UpdateState();
        editor._finished = true; // при выходе из режима рендера не спрашивать о сохранении
        return editor;
    }

    /// <summary>Пикселей картинки на один пиксель окна.</summary>
    private double Scale => Fit.ActualWidth > 0 ? _image.PixelWidth / Fit.ActualWidth : 1;

    // ---------------------------------------------------------------- инструменты и параметры

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        CommitText();
        CommitOverlay();
        CancelSelection();
        _tool = sender switch
        {
            _ when sender == CropTool => Tool.Crop,
            _ when sender == MarkerTool => Tool.Marker,
            _ when sender == ArrowTool => Tool.Arrow,
            _ when sender == LineTool => Tool.Line,
            _ when sender == RectangleTool => Tool.Rectangle,
            _ when sender == EllipseTool => Tool.Ellipse,
            _ when sender == TextTool => Tool.Text,
            _ when sender == PixelateTool => Tool.Pixelate,
            _ => Tool.Pen,
        };
        if (SizeSlider is null)
            return; // ещё строится окно
        var text = _tool == Tool.Text;
        SizeLabel.Text = text ? "Размер" : "Толщина";
        SizeSlider.Minimum = text ? 10 : 1;
        SizeSlider.Maximum = text ? 120 : 40;
        SizeSlider.Value = text ? _textSize : _strokeSize;
        UpdateBars();
        UpdateCursor();
        UpdateState();
    }

    /// <summary>Сверху — цвет и толщина для рисования, режимы для обрезки; пока двигают картинку поверх — ни то ни другое.</summary>
    private void UpdateBars()
    {
        StyleBar.Visibility = _overlay is null && _tool is not (Tool.Crop or Tool.Pixelate) ? Visibility.Visible : Visibility.Collapsed;
        CropModes.Visibility = _overlay is null && _tool == Tool.Crop ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCropModeChecked(object sender, RoutedEventArgs e)
    {
        _cropMode = sender == CropCircle ? CropMode.Circle : sender == CropLasso ? CropMode.Lasso : CropMode.Rectangle;
        if (CropBar is null)
            return;
        CancelSelection();
    }

    private void OnColorChecked(object sender, RoutedEventArgs e)
    {
        _color = ((SolidColorBrush)((RadioButton)sender).Background).Color;
        if (_textBox is not null)
            _textBox.Foreground = _textBox.CaretBrush = new SolidColorBrush(_color);
    }

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = Math.Round(e.NewValue);
        if (SizeValue is null)
            return;
        SizeValue.Text = value.ToString("0");
        if (_tool == Tool.Text)
        {
            _textSize = value;
            if (_textBox is not null)
                _textBox.FontSize = value * Scale;
        }
        else
            _strokeSize = value;
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => Dispatcher.BeginInvoke(SyncScale);

    private void SyncScale()
    {
        Surface.Scale = Scale;
        if (_overlay is not null)
        {
            _overlay.Scale = Scale;
            Surface.InvalidateVisual();
        }
    }

    private void UpdateCursor() =>
        Surface.Cursor = _overlay is not null ? Cursors.SizeAll : _tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;

    // ---------------------------------------------------------------- мышь на холсте

    private Point Clamp(Point point) =>
        new(Math.Clamp(point.X, 0, _image.PixelWidth), Math.Clamp(point.Y, 0, _image.PixelHeight));

    private void OnSurfaceDown(object sender, MouseButtonEventArgs e)
    {
        CommitText();
        SyncScale();
        if (_overlay is { } overlay)
        {
            // Пока картинку поверх не вставили — мышь двигает её и тянет за уголок
            var at = e.GetPosition(Surface);
            var handle = overlay.Handle;
            handle.Inflate(overlay.HandleSize / 2, overlay.HandleSize / 2);
            _overlayDrag = handle.Contains(at) ? OverlayDrag.Resize : overlay.Place.Contains(at) ? OverlayDrag.Move : OverlayDrag.None;
            if (_overlayDrag != OverlayDrag.None)
            {
                _dragStart = at;
                _overlayStart = overlay.Place;
                _dragging = true;
                Surface.CaptureMouse();
            }
            e.Handled = true;
            return;
        }

        var point = Clamp(e.GetPosition(Surface));
        if (_tool == Tool.Text)
        {
            BeginText(point);
            e.Handled = true;
            return;
        }
        _dragStart = point;
        _dragging = true;
        Surface.CaptureMouse();
        var width = _strokeSize * Scale;
        switch (_tool)
        {
            case Tool.Pen or Tool.Marker:
                var stroke = new StrokeAnnotation(_color, _tool == Tool.Marker ? width * 4 : width, _tool == Tool.Marker);
                stroke.Add(point);
                _drawing = stroke;
                break;
            case Tool.Arrow or Tool.Line or Tool.Rectangle or Tool.Ellipse:
                var kind = _tool switch
                {
                    Tool.Arrow => ShapeKind.Arrow,
                    Tool.Line => ShapeKind.Line,
                    Tool.Rectangle => ShapeKind.Rectangle,
                    _ => ShapeKind.Ellipse,
                };
                _drawing = new ShapeAnnotation(kind, _color, width, point);
                break;
            default:
                Surface.DimOutside = _tool == Tool.Crop;
                Surface.Selection = null;
                CropBar.Visibility = Visibility.Collapsed;
                _lasso = _tool == Tool.Crop && _cropMode == CropMode.Lasso ? [point] : null;
                break;
        }
        Surface.Preview = _drawing;
        e.Handled = true;
    }

    private void OnSurfaceMove(object sender, MouseEventArgs e)
    {
        if (_overlay is { } overlay)
        {
            var at = e.GetPosition(Surface);
            if (!_dragging)
            {
                var handle = overlay.Handle;
                handle.Inflate(overlay.HandleSize / 2, overlay.HandleSize / 2);
                Surface.Cursor = handle.Contains(at) ? Cursors.SizeNWSE : overlay.Place.Contains(at) ? Cursors.SizeAll : Cursors.Arrow;
                return;
            }
            var delta = at - _dragStart;
            if (_overlayDrag == OverlayDrag.Move)
                overlay.Place = new Rect(_overlayStart.TopLeft + delta, _overlayStart.Size);
            else
            {
                // Пропорции картинки сохраняются, тянем за правый нижний угол
                var width = Math.Max(16 * Scale, _overlayStart.Width + Math.Max(delta.X, delta.Y * _overlayStart.Width / _overlayStart.Height));
                overlay.Place = new Rect(_overlayStart.TopLeft, new Size(width, width * _overlayStart.Height / _overlayStart.Width));
            }
            Surface.InvalidateVisual();
            return;
        }

        if (!_dragging)
            return;
        var point = Clamp(e.GetPosition(Surface));
        var straight = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (_drawing)
        {
            case StrokeAnnotation stroke:
                stroke.Add(point);
                Surface.InvalidateVisual();
                break;
            case ShapeAnnotation shape:
                shape.End = straight ? Constrain(_dragStart, point, _tool is Tool.Rectangle or Tool.Ellipse) : point;
                Surface.InvalidateVisual();
                break;
            default:
                Surface.Selection = SelectionTo(point);
                if (_tool == Tool.Crop)
                    ShowCropSize();
                break;
        }
    }

    /// <summary>Контур выделения от точки нажатия до мыши: рамка, круг или линия пера.</summary>
    private Geometry SelectionTo(Point point)
    {
        Geometry geometry;
        if (_tool == Tool.Crop && _cropMode == CropMode.Circle)
            geometry = new EllipseGeometry(new Rect(_dragStart, Constrain(_dragStart, point, square: true)));
        else if (_lasso is { } lasso)
        {
            if ((point - lasso[^1]).Length >= Scale)
                lasso.Add(point);
            var path = new StreamGeometry();
            using (var ctx = path.Open())
            {
                ctx.BeginFigure(lasso[0], true, true);
                ctx.PolyLineTo(lasso.Skip(1).ToList(), true, true);
            }
            geometry = path;
        }
        else
            geometry = new RectangleGeometry(new Rect(_dragStart, point));
        geometry.Freeze();
        return geometry;
    }

    private void OnSurfaceUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
            Surface.ReleaseMouseCapture(); // дальше — OnSurfaceLostCapture
    }

    private void OnSurfaceLostCapture(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        if (_overlay is not null)
        {
            _overlayDrag = OverlayDrag.None;
            return;
        }
        switch (_drawing)
        {
            case StrokeAnnotation stroke:
                Apply(BitmapOps.Bake(_image, stroke));
                break;
            case ShapeAnnotation shape when !shape.IsEmpty:
                Apply(BitmapOps.Bake(_image, shape));
                break;
            case null when Surface.Selection is { } area:
                if (BitmapOps.Clip(_image, area.Bounds) is not { } pixels || _lasso is { Count: < 3 })
                    CancelSelection();
                else if (_tool == Tool.Pixelate)
                {
                    CancelSelection();
                    Apply(BitmapOps.Pixelate(_image, pixels));
                }
                else
                    ShowApplyBar("Обрезать");
                break;
        }
        _lasso = null;
        _drawing = null;
        Surface.Preview = null;
    }

    /// <summary>С Shift: линии — по 45°, рамки и овалы — квадрат и круг.</summary>
    private static Point Constrain(Point start, Point end, bool square)
    {
        var delta = end - start;
        if (square)
        {
            var side = Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
            return new Point(start.X + Math.Sign(delta.X) * side, start.Y + Math.Sign(delta.Y) * side);
        }
        var angle = Math.Round(Math.Atan2(delta.Y, delta.X) / (Math.PI / 4)) * (Math.PI / 4);
        return start + new Vector(Math.Cos(angle), Math.Sin(angle)) * delta.Length;
    }

    // ---------------------------------------------------------------- обрезка

    private void ShowApplyBar(string action)
    {
        ApplyText.Text = action;
        CropBar.Visibility = Visibility.Visible;
        UpdateState();
    }

    private void ShowCropSize()
    {
        if (Surface.Selection is { } area && BitmapOps.Clip(_image, area.Bounds) is { } pixels)
            CropSize.Text = $"{pixels.Width} × {pixels.Height}";
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_overlay is not null)
            CommitOverlay();
        else
            ApplyCrop();
    }

    private void OnCancelApply(object sender, RoutedEventArgs e)
    {
        if (_overlay is not null)
            CancelOverlay();
        else
            CancelSelection();
    }

    private void ApplyCrop()
    {
        if (Surface.Selection is not { } shape || BitmapOps.Clip(_image, shape.Bounds) is not { } pixels)
            return;
        var rectangle = shape is RectangleGeometry;
        CancelSelection();
        Apply(rectangle ? BitmapOps.Crop(_image, pixels) : BitmapOps.CropToShape(_image, shape, pixels));
        _needsAlpha |= !rectangle;
    }

    private void CancelSelection()
    {
        Surface.Selection = null;
        _lasso = null;
        if (_overlay is null)
            CropBar.Visibility = Visibility.Collapsed;
        UpdateState();
    }

    // ---------------------------------------------------------------- текст

    private void BeginText(Point at)
    {
        _textAt = at;
        var scale = Scale;
        var box = new TextBox
        {
            Style = null,
            FontFamily = TextAnnotation.Font,
            FontWeight = FontWeights.SemiBold,
            FontSize = _textSize * scale,
            Foreground = new SolidColorBrush(_color),
            CaretBrush = new SolidColorBrush(_color),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(scale),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            MinWidth = 40 * scale,
        };
        // Текст в поле начинается на рамку и на 2 единицы правее своего края — сдвигаем, чтобы надпись осталась на месте
        Canvas.SetLeft(box, at.X - scale - 2);
        Canvas.SetTop(box, at.Y - scale);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                CommitText();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelText();
                e.Handled = true;
            }
        };
        TextLayer.Width = _image.PixelWidth;
        TextLayer.Height = _image.PixelHeight;
        TextLayer.Children.Add(box);
        _textBox = box;
        Dispatcher.BeginInvoke(() => box.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CommitText()
    {
        if (_textBox is not { } box)
            return;
        CancelText();
        if (!string.IsNullOrWhiteSpace(box.Text))
            Apply(BitmapOps.Bake(_image, new TextAnnotation(box.Text.TrimEnd(), _textAt, box.FontSize, _color)));
    }

    private void CancelText()
    {
        if (_textBox is null)
            return;
        TextLayer.Children.Remove(_textBox);
        _textBox = null;
    }

    // ---------------------------------------------------------------- поворот, отражение, добавление изображения

    private void OnRotate(object sender, RoutedEventArgs e) => Edit(() => BitmapOps.Rotate(_image, 90));

    private void OnFlip(object sender, RoutedEventArgs e) => Edit(() => BitmapOps.Flip(_image));

    private void Edit(Func<BitmapSource> edit)
    {
        CommitText();
        CommitOverlay();
        CancelSelection();
        Apply(edit());
    }

    private void OnMerge(object sender, RoutedEventArgs e) => MergeMenu.IsOpen = true;

    private async void OnMergeSide(object sender, RoutedEventArgs e)
    {
        MergeMenu.IsOpen = false;
        var where = (string)((Button)sender).Tag;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Какое изображение добавить?",
            Filter = ImageFilter,
            InitialDirectory = _source.Folder,
        };
        if (dialog.ShowDialog(this) == true)
            await AddImage(dialog.FileName, where == "Over" ? null : Enum.Parse<Side>(where));
    }

    /// <summary>Картинку можно и просто перетащить в окно — она встанет поверх, её можно подвинуть и растянуть.</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && Formats.IsPicture(new FileItem(files[0])))
            await AddImage(files[0], null);
    }

    /// <summary>Приставить изображение с нужной стороны или (side = null) положить поверх.</summary>
    private async Task AddImage(string path, Side? side)
    {
        CommitText();
        CommitOverlay();
        CancelSelection();
        HintText.Text = "Открываю изображение…";
        try
        {
            var picture = BitmapOps.Decode(await Task.Run(() => BitmapOps.ReadPng(path)));
            if (side is { } s)
                Apply(BitmapOps.Merge(_image, picture, s));
            else
                BeginOverlay(picture, Scale);
        }
        catch (Exception ex)
        {
            UpdateState();
            MessageBox.Show(this, $"Не удалось открыть изображение: {ex.Message}", "Orbit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Картинка поверх: сначала по центру и не больше 40 % холста, дальше её двигают и тянут за уголок.</summary>
    private void BeginOverlay(BitmapSource picture, double scale)
    {
        var fit = Math.Min(1, Math.Min(_image.PixelWidth * 0.4 / picture.PixelWidth, _image.PixelHeight * 0.4 / picture.PixelHeight));
        var size = new Size(picture.PixelWidth * fit, picture.PixelHeight * fit);
        var place = new Rect(new Point((_image.PixelWidth - size.Width) / 2, (_image.PixelHeight - size.Height) / 2), size);
        _overlay = new OverlayAnnotation(picture, place) { Scale = scale };
        Surface.Preview = _overlay;
        ShowApplyBar("Вставить");
        CropSize.Text = "";
        UpdateBars();
        UpdateCursor();
    }

    private void CommitOverlay()
    {
        if (_overlay is not { } overlay)
            return;
        CancelOverlay();
        Apply(BitmapOps.Overlay(_image, overlay.Image, overlay.Place));
    }

    private void CancelOverlay()
    {
        if (_overlay is null)
            return;
        _overlay = null;
        _overlayDrag = OverlayDrag.None;
        Surface.Preview = null;
        CropBar.Visibility = Visibility.Collapsed;
        UpdateBars();
        UpdateCursor();
        UpdateState();
    }

    // ---------------------------------------------------------------- история

    private void Apply(BitmapSource next)
    {
        _undo.Add(_image);
        _redo.Clear();
        long bytes = 0;
        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            bytes += 4L * _undo[i].PixelWidth * _undo[i].PixelHeight;
            if (bytes > UndoBudget && _undo.Count - i > 3)
            {
                _undo.RemoveRange(0, i + 1);
                break;
            }
        }
        Show(next);
        _dirty = true;
    }

    private void Show(BitmapSource image)
    {
        _image = image;
        Surface.Image = image;
        UpdateState();
        Dispatcher.BeginInvoke(SyncScale, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        CancelText();
        CancelOverlay();
        CancelSelection();
        if (_undo.Count == 0)
            return;
        _redo.Add(_image);
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Show(previous);
        _dirty = true;
    }

    private void Redo()
    {
        CancelText();
        CancelOverlay();
        CancelSelection();
        if (_redo.Count == 0)
            return;
        _undo.Add(_image);
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Show(next);
        _dirty = true;
    }

    private void UpdateState()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        SizeText.Text = $"{_image.PixelWidth} × {_image.PixelHeight} px";
        var applying = CropBar.Visibility == Visibility.Visible;
        HintText.Text = _tool switch
        {
            _ when _overlay is not null => "Двигайте картинку мышью, тяните за уголок, чтобы изменить размер. Enter — вставить, Esc — отменить",
            Tool.Crop when applying => "Enter — обрезать, Esc — отменить, можно выделить заново",
            Tool.Crop when _cropMode == CropMode.Circle => "Ведите мышью — получится круг; углы вокруг станут прозрачными",
            Tool.Crop when _cropMode == CropMode.Lasso => "Обведите нужное пером — контур замкнётся сам; всё вокруг станет прозрачным",
            Tool.Crop => "Выделите область, которую нужно оставить",
            Tool.Text => "Щёлкните, где нужна надпись. Enter — готово, Shift+Enter — новая строка",
            Tool.Pixelate => "Выделите, что скрыть: номер, лицо, пароль",
            Tool.Arrow or Tool.Line => "Ведите мышью; с Shift — ровно по 45°",
            Tool.Rectangle or Tool.Ellipse => "Ведите мышью; с Shift — квадрат или круг",
            _ => "Рисуйте мышью. Другое изображение можно просто перетащить в окно — оно встанет поверх",
        };
    }

    // ---------------------------------------------------------------- копирование и сохранение

    private void OnCopy(object sender, RoutedEventArgs e) => Copy();

    private void Copy()
    {
        CommitText();
        CommitOverlay();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetImage(_image);
                HintText.Text = "Картинка скопирована — вставьте её через Ctrl+V";
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(60); // буфер занят другим приложением
            }
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        CommitText();
        CommitOverlay();
        CancelSelection();
        SaveButton.IsEnabled = false;
        try
        {
            var format = _needsAlpha && _format is "jpg" or "bmp" ? "png" : _format;
            var png = BitmapOps.EncodePng(_image);
            var path = await OutputPaths.ProduceAsync(_outputDir, $"{_source.BaseName} (изменено)", format,
                temp => ImageBackend.SaveAsync(png, temp, format, _quality, CancellationToken.None));
            _finished = true;
            _result.TrySetResult([path]);
            Close();
        }
        catch (Exception ex)
        {
            SaveButton.IsEnabled = true;
            MessageBox.Show(this, $"Не удалось сохранить: {ex.Message}", "Orbit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_finished || (!_dirty && _textBox is null && _overlay is null))
            return;
        var answer = MessageBox.Show(this, "Сохранить изменения? Исходный файл не изменится — рядом появится копия.", "Orbit",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            e.Cancel = true;
            _ = SaveAsync();
        }
        else if (answer == MessageBoxResult.Cancel)
            e.Cancel = true;
    }

    // ---------------------------------------------------------------- клавиатура

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_textBox is { IsKeyboardFocusWithin: true })
            return; // клавиши — в надпись
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        RadioButton? tool = null;
        switch (e.Key)
        {
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                Redo();
                break;
            case Key.Z when ctrl:
                Undo();
                break;
            case Key.S when ctrl:
                _ = SaveAsync();
                break;
            case Key.C when ctrl:
                Copy();
                break;
            case Key.Enter when _overlay is not null:
                CommitOverlay();
                break;
            case Key.Escape when _overlay is not null:
                CancelOverlay();
                break;
            case Key.Enter when Surface.Selection is not null && _tool == Tool.Crop:
                ApplyCrop();
                break;
            case Key.Escape when Surface.Selection is not null:
                CancelSelection();
                break;
            case Key.Escape:
                Close();
                break;
            case Key.C when !ctrl: tool = CropTool; break;
            case Key.B: tool = PenTool; break;
            case Key.H: tool = MarkerTool; break;
            case Key.A when !ctrl: tool = ArrowTool; break;
            case Key.L: tool = LineTool; break;
            case Key.R: tool = RectangleTool; break;
            case Key.E: tool = EllipseTool; break;
            case Key.T: tool = TextTool; break;
            case Key.X when !ctrl: tool = PixelateTool; break;
            default:
                return;
        }
        if (tool is not null)
            tool.IsChecked = true;
        e.Handled = true;
    }
}
