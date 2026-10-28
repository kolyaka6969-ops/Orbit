using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbit.UI.Editor;

/// <summary>
/// Холст редактора: картинка 1:1 (одна единица — один пиксель картинки, на экран её ужимает Viewbox),
/// пометка, которую сейчас ведут, и контур выделения — прямоугольник, круг или произвольный контур пера.
/// </summary>
internal sealed class EditorSurface : FrameworkElement
{
    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
    private BitmapSource? _image;
    private Annotation? _preview;
    private Geometry? _selection;

    public BitmapSource? Image
    {
        get => _image;
        set
        {
            _image = value;
            Width = value?.PixelWidth ?? 0;
            Height = value?.PixelHeight ?? 0;
            InvalidateVisual();
        }
    }

    public Annotation? Preview
    {
        get => _preview;
        set
        {
            _preview = value;
            InvalidateVisual();
        }
    }

    /// <summary>Контур выделения в пикселях картинки.</summary>
    public Geometry? Selection
    {
        get => _selection;
        set
        {
            _selection = value;
            InvalidateVisual();
        }
    }

    /// <summary>Затемнять всё вне выделения (обрезка) или только обводить его (мозаика).</summary>
    public bool DimOutside { get; set; }

    /// <summary>Пикселей картинки на один пиксель экрана: рамка и шахматка остаются тонкими при любом масштабе.</summary>
    public double Scale { get; set; } = 1;

    protected override void OnRender(DrawingContext dc)
    {
        if (_image is null)
            return;
        var full = new Rect(0, 0, _image.PixelWidth, _image.PixelHeight);
        dc.DrawRectangle(Checker(8 * Scale), null, full);
        dc.DrawImage(_image, full);
        _preview?.Draw(dc);
        if (_selection is not { } selection)
            return;
        if (DimOutside)
            dc.DrawGeometry(Dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), selection));
        DrawOutline(dc, selection, Scale);
    }

    /// <summary>Пунктир белым по чёрному — виден на любой картинке.</summary>
    public static void DrawOutline(DrawingContext dc, Geometry geometry, double scale)
    {
        var thickness = 1.5 * scale;
        dc.DrawGeometry(null, new Pen(Brushes.Black, thickness), geometry);
        dc.DrawGeometry(null, new Pen(Brushes.White, thickness) { DashStyle = new DashStyle([4, 4], 0) }, geometry);
    }

    /// <summary>Шахматка под прозрачными местами, как в графических редакторах.</summary>
    private static Brush Checker(double cell)
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 2, 2))));
        var gray = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4));
        drawing.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        drawing.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(1, 1, 1, 1))));
        return Frozen(new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, cell * 2, cell * 2),
            ViewportUnits = BrushMappingMode.Absolute,
        });
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
