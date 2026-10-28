using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Orbit.UI.Editor;

/// <summary>
/// Пометка поверх картинки. Рисуется одинаково и на экране, пока её ведут мышью, и при впечатывании в картинку.
/// Координаты и толщины — в пикселях картинки.
/// </summary>
internal abstract class Annotation
{
    public abstract void Draw(DrawingContext dc);

    protected static Pen RoundPen(Brush brush, double width) => new(brush, width)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };
}

/// <summary>Кисть или маркер: линия по точкам. Маркер полупрозрачный — одной фигурой, чтобы не темнел на пересечениях.</summary>
internal sealed class StrokeAnnotation(Color color, double width, bool marker) : Annotation
{
    private readonly List<Point> _points = [];

    public void Add(Point point)
    {
        if (_points.Count == 0 || (point - _points[^1]).Length >= 0.75)
            _points.Add(point);
    }

    public override void Draw(DrawingContext dc)
    {
        if (_points.Count == 0)
            return;
        var brush = new SolidColorBrush(marker ? Color.FromArgb(0x70, color.R, color.G, color.B) : color);
        if (_points.Count == 1)
        {
            dc.DrawEllipse(brush, null, _points[0], width / 2, width / 2);
            return;
        }
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(_points[0], false, false);
            ctx.PolyLineTo(_points.Skip(1).ToList(), true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, RoundPen(brush, width), geometry);
    }
}

internal enum ShapeKind
{
    Line,
    Arrow,
    Rectangle,
    Ellipse,
}

internal sealed class ShapeAnnotation(ShapeKind kind, Color color, double width, Point start) : Annotation
{
    private readonly Point _start = start;

    public Point End { get; set; } = start;

    public bool IsEmpty => (End - _start).Length < 2;

    public override void Draw(DrawingContext dc)
    {
        if (IsEmpty)
            return;
        var brush = new SolidColorBrush(color);
        var pen = RoundPen(brush, width);
        var rect = new Rect(_start, End);
        switch (kind)
        {
            case ShapeKind.Line:
                dc.DrawLine(pen, _start, End);
                break;
            case ShapeKind.Rectangle:
                dc.DrawRectangle(null, pen, rect);
                break;
            case ShapeKind.Ellipse:
                dc.DrawEllipse(null, pen, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
                break;
            case ShapeKind.Arrow:
                DrawArrow(dc, brush, pen);
                break;
        }
    }

    private void DrawArrow(DrawingContext dc, Brush brush, Pen pen)
    {
        var direction = End - _start;
        var length = direction.Length;
        direction /= length;
        // Наконечник растёт с толщиной, но не длиннее большей части стрелки
        var head = Math.Min(length * 0.6, Math.Max(width * 4, 14));
        var normal = new Vector(-direction.Y, direction.X) * head * 0.55;
        var neck = End - direction * head;
        dc.DrawLine(pen, _start, neck + direction * Math.Min(width, head / 2));
        var tip = new StreamGeometry();
        using (var ctx = tip.Open())
        {
            ctx.BeginFigure(End, true, true);
            ctx.LineTo(neck + normal, true, true);
            ctx.LineTo(neck - normal, true, true);
        }
        tip.Freeze();
        dc.DrawGeometry(brush, RoundPen(brush, Math.Max(1, width / 2)), tip);
    }
}

/// <summary>Надпись. Тонкий контур контрастного цвета — чтобы читалась и на светлом, и на тёмном.</summary>
internal sealed class TextAnnotation(string text, Point at, double size, Color color) : Annotation
{
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");

    public override void Draw(DrawingContext dc)
    {
        var brush = new SolidColorBrush(color);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size, brush, 1.0);
        var geometry = formatted.BuildGeometry(at);
        var light = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 140;
        var outline = new SolidColorBrush(light ? Color.FromArgb(0x99, 0, 0, 0) : Color.FromArgb(0x99, 255, 255, 255));
        dc.DrawGeometry(null, RoundPen(outline, Math.Max(1, size / 14)), geometry);
        dc.DrawGeometry(brush, null, geometry);
    }
}

/// <summary>Картинка поверх, пока её двигают и растягивают: рамка и уголок, за который тянуть.</summary>
internal sealed class OverlayAnnotation(System.Windows.Media.Imaging.BitmapSource image, Rect place) : Annotation
{
    public System.Windows.Media.Imaging.BitmapSource Image { get; } = image;

    public Rect Place { get; set; } = place;

    /// <summary>Пикселей картинки на пиксель экрана — рамка и уголок одинаковые при любом масштабе.</summary>
    public double Scale { get; set; } = 1;

    public double HandleSize => 14 * Scale;

    public Rect Handle => new(Place.Right - HandleSize / 2, Place.Bottom - HandleSize / 2, HandleSize, HandleSize);

    public override void Draw(DrawingContext dc)
    {
        dc.DrawImage(Image, Place);
        EditorSurface.DrawOutline(dc, new RectangleGeometry(Place), Scale);
        dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, Scale), Handle);
    }
}
