using System.Windows;
using System.Windows.Media;

namespace Orbit.UI.Editor;

/// <summary>
/// Область приближения поверх кадра видео, в пикселях исходника: всё вне её затемнено.
/// Мышь обрабатывает окно видеоредактора, здесь — только отрисовка.
/// </summary>
internal sealed class ZoomOverlay : FrameworkElement
{
    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
    private Rect? _area;

    public Rect? Area
    {
        get => _area;
        set
        {
            _area = value;
            InvalidateVisual();
        }
    }

    /// <summary>Пикселей видео на пиксель экрана — рамка остаётся тонкой.</summary>
    public double Scale { get; set; } = 1;

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, full); // ловим мышь по всему кадру
        if (_area is not { } area)
            return;
        dc.DrawGeometry(Dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(area)));
        EditorSurface.DrawOutline(dc, new RectangleGeometry(area), Scale);
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
