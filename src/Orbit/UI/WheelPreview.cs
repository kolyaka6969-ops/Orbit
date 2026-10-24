using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Orbit.UI;

/// <summary>Маленький круг на главной странице настроек: показывает, как выглядит меню, и медленно «перебирает» сектора.</summary>
public sealed class WheelPreview : FrameworkElement
{
    private static readonly string[] Labels = ["PNG", "JPG", "WEBP", "AVIF", "PDF", "ICO"];

    public static readonly DependencyProperty HighlightProperty =
        DependencyProperty.Register(nameof(Highlight), typeof(double), typeof(WheelPreview),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public WheelPreview()
    {
        Loaded += (_, _) =>
        {
            ThemeManager.Changed += InvalidateVisual;
            var cycle = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(Labels.Length * 1.2) };
            for (var i = 0; i < Labels.Length; i++)
                cycle.KeyFrames.Add(new DiscreteDoubleKeyFrame(i, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(i * 1.2))));
            BeginAnimation(HighlightProperty, cycle);
        };
        Unloaded += (_, _) => ThemeManager.Changed -= InvalidateVisual;
    }

    public double Highlight
    {
        get => (double)GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
            return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var outer = size / 2 - 4;
        var inner = outer * 0.4;
        var n = Labels.Length;
        var span = 360.0 / n;
        var active = (int)Highlight;

        Brush Res(string key) => (Brush)FindResource(key);
        dc.DrawEllipse(Res("WheelFill"), new Pen(Res("WheelStroke"), 1), center, outer + 4, outer + 4);

        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i < n; i++)
        {
            var mid = (-90 + i * span) * Math.PI / 180;
            var fill = i == active ? Res("AccentBrush") : Res("SliceFill");
            var geometry = Slice(center, inner + 3, outer - 3, mid - span / 2 * Math.PI / 180, mid + span / 2 * Math.PI / 180);
            var pop = i == active ? 4 : 0;
            dc.PushTransform(new TranslateTransform(pop * Math.Cos(mid), pop * Math.Sin(mid)));
            dc.DrawGeometry(fill, new Pen(fill, 5) { LineJoin = PenLineJoin.Round }, geometry);
            var text = new FormattedText(Labels[i], System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11,
                i == active ? Res("TextOnAccent") : Res("TextPrimary"), dpi);
            var r = (inner + outer) / 2;
            dc.DrawText(text, new Point(center.X + r * Math.Cos(mid) - text.Width / 2, center.Y + r * Math.Sin(mid) - text.Height / 2));
            dc.Pop();
        }
        dc.DrawEllipse(Res("WheelHubFill"), new Pen(Res("WheelStroke"), 1), center, inner - 3, inner - 3);
        var hub = new FormattedText("", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 20, Res("AccentTextBrush"), dpi);
        dc.DrawText(hub, new Point(center.X - hub.Width / 2, center.Y - hub.Height / 2));
    }

    private static Geometry Slice(Point c, double rIn, double rOut, double a0, double a1)
    {
        const double halfGap = 4;
        var gOut = Math.Asin(halfGap / rOut);
        var gIn = Math.Asin(halfGap / rIn);
        Point P(double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(rOut, a0 + gOut), true, true);
            ctx.ArcTo(P(rOut, a1 - gOut), new Size(rOut, rOut), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(P(rIn, a1 - gIn), true, true);
            ctx.ArcTo(P(rIn, a0 + gIn), new Size(rIn, rIn), 0, false, SweepDirection.Counterclockwise, true, true);
        }
        g.Freeze();
        return g;
    }
}
