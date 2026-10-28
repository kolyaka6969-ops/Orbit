using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orbit.Core.Backends;

namespace Orbit.UI.Editor;

internal enum Side
{
    Right,
    Bottom,
    Left,
    Top,
}

/// <summary>
/// Правки картинки в редакторе. Каждая возвращает новую замороженную картинку, старая остаётся для отмены.
/// Рисование идёт в масштабе 1:1 (96 DPI), поэтому DPI исходного файла на размер не влияет.
/// </summary>
internal static class BitmapOps
{
    /// <summary>Любой формат, который читает Orbit (в том числе HEIC, RAW, WEBP), — через ImageMagick в PNG.</summary>
    public static byte[] ReadPng(string path) => ImageBackend.ToPng(path, uint.MaxValue);

    public static BitmapSource Decode(byte[] png)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource Render(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            draw(dc);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Rect Full(BitmapSource image) => new(0, 0, image.PixelWidth, image.PixelHeight);

    /// <summary>Впечатать пометку в картинку.</summary>
    public static BitmapSource Bake(BitmapSource image, Annotation annotation) =>
        Render(image.PixelWidth, image.PixelHeight, dc =>
        {
            dc.DrawImage(image, Full(image));
            annotation.Draw(dc);
        });

    /// <summary>Прямоугольник в пикселях, обрезанный по краям картинки; null — слишком маленький.</summary>
    public static Int32Rect? Clip(BitmapSource image, Rect area)
    {
        var x = (int)Math.Floor(Math.Max(0, area.Left));
        var y = (int)Math.Floor(Math.Max(0, area.Top));
        var right = (int)Math.Ceiling(Math.Min(image.PixelWidth, area.Right));
        var bottom = (int)Math.Ceiling(Math.Min(image.PixelHeight, area.Bottom));
        return right - x >= 2 && bottom - y >= 2 ? new Int32Rect(x, y, right - x, bottom - y) : null;
    }

    public static BitmapSource Crop(BitmapSource image, Int32Rect area)
    {
        var cropped = new CroppedBitmap(image, area);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>
    /// Обрезка по контуру (круг, перо): остаётся описанный прямоугольник, всё вне контура — прозрачное.
    /// </summary>
    public static BitmapSource CropToShape(BitmapSource image, Geometry shape, Int32Rect area) =>
        Render(area.Width, area.Height, dc =>
        {
            dc.PushTransform(new TranslateTransform(-area.X, -area.Y));
            dc.PushClip(shape);
            dc.DrawImage(image, Full(image));
            dc.Pop();
            dc.Pop();
        });

    /// <summary>Вставить картинку поверх текущей в заданное место.</summary>
    public static BitmapSource Overlay(BitmapSource image, BitmapSource overlay, Rect place) =>
        Render(image.PixelWidth, image.PixelHeight, dc =>
        {
            dc.DrawImage(image, Full(image));
            dc.DrawImage(overlay, place);
        });

    public static BitmapSource Rotate(BitmapSource image, int degrees) => Transform(image, new RotateTransform(degrees));

    public static BitmapSource Flip(BitmapSource image) => Transform(image, new ScaleTransform(-1, 1));

    private static BitmapSource Transform(BitmapSource image, Transform transform)
    {
        var result = new TransformedBitmap(image, transform);
        result.Freeze();
        return result;
    }

    /// <summary>Скрыть область крупной мозаикой: номер, лицо, пароль на снимке экрана.</summary>
    public static BitmapSource Pixelate(BitmapSource image, Int32Rect area)
    {
        var block = Math.Max(6, Math.Min(area.Width, area.Height) / 8);
        var columns = Math.Max(1, area.Width / block);
        var rows = Math.Max(1, area.Height / block);
        // Уменьшение усредняет цвет каждого квадрата; увеличиваем обратно сами — WPF при отрисовке сгладил бы квадраты в размытие
        var small = new FormatConvertedBitmap(
            new TransformedBitmap(new CroppedBitmap(image, area), new ScaleTransform(columns / (double)area.Width, rows / (double)area.Height)),
            PixelFormats.Bgra32, null, 0);
        var colors = new int[columns * rows];
        small.CopyPixels(colors, columns * 4, 0);
        var pixels = new int[area.Width * area.Height];
        for (var y = 0; y < area.Height; y++)
        {
            var row = Math.Min(rows - 1, y * rows / area.Height) * columns;
            for (var x = 0; x < area.Width; x++)
                pixels[y * area.Width + x] = colors[row + Math.Min(columns - 1, x * columns / area.Width)];
        }
        var mosaic = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgra32, null, pixels, area.Width * 4);
        mosaic.Freeze();
        return Render(image.PixelWidth, image.PixelHeight, dc =>
        {
            dc.DrawImage(image, Full(image));
            dc.DrawImage(mosaic, new Rect(area.X, area.Y, area.Width, area.Height));
        });
    }

    /// <summary>
    /// Две картинки в одну: вторая приставляется с нужной стороны и масштабируется под высоту (сбоку)
    /// или ширину (сверху и снизу) первой.
    /// </summary>
    public static BitmapSource Merge(BitmapSource first, BitmapSource second, Side side)
    {
        var row = side is Side.Right or Side.Left;
        var scale = row ? first.PixelHeight / (double)second.PixelHeight : first.PixelWidth / (double)second.PixelWidth;
        var width2 = Math.Max(1, (int)Math.Round(second.PixelWidth * scale));
        var height2 = Math.Max(1, (int)Math.Round(second.PixelHeight * scale));
        var width = row ? first.PixelWidth + width2 : first.PixelWidth;
        var height = row ? first.PixelHeight : first.PixelHeight + height2;
        var (a, b) = side switch
        {
            Side.Right => (new Point(0, 0), new Point(first.PixelWidth, 0)),
            Side.Left => (new Point(width2, 0), new Point(0, 0)),
            Side.Bottom => (new Point(0, 0), new Point(0, first.PixelHeight)),
            _ => (new Point(0, height2), new Point(0, 0)),
        };
        return Render(width, height, dc =>
        {
            dc.DrawImage(first, new Rect(a, new Size(first.PixelWidth, first.PixelHeight)));
            dc.DrawImage(second, new Rect(b, new Size(width2, height2)));
        });
    }
}
