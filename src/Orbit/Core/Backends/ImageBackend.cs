using ImageMagick;

namespace Orbit.Core.Backends;

/// <summary>Изображения и RAW через Magick.NET (ImageMagick + libraw + libheif, всё внутри приложения).</summary>
public static class ImageBackend
{
    /// <summary>Во что умеем конвертировать: (расширение, подпись в меню).</summary>
    public static readonly (string Ext, string? Subtitle)[] Targets =
    [
        ("png", null),
        ("jpg", null),
        ("webp", null),
        ("avif", null),
        ("tiff", null),
        ("gif", null),
        ("ico", "значок"),
        ("pdf", "документ"),
    ];

    private static MagickFormat FormatOf(string ext) => ext switch
    {
        "png" => MagickFormat.Png,
        "jpg" => MagickFormat.Jpeg,
        "webp" => MagickFormat.WebP,
        "avif" => MagickFormat.Avif,
        "tiff" => MagickFormat.Tiff,
        "gif" => MagickFormat.Gif,
        "ico" => MagickFormat.Icon,
        "pdf" => MagickFormat.Pdf,
        "bmp" => MagickFormat.Bmp,
        _ => throw new ArgumentOutOfRangeException(nameof(ext), ext, null),
    };

    /// <summary>Формат результата для правок (поворот, сжатие…): тот же, если в него можно писать.</summary>
    public static string EditFormatFor(FileItem item)
    {
        var ext = Formats.Canonical(item.Extension);
        return ext switch
        {
            "png" or "jpg" or "webp" or "avif" or "tiff" or "gif" or "bmp" => ext,
            "psd" or "tga" or "ico" or "qoi" => "png",
            _ => "jpg", // RAW, HEIC, JXL
        };
    }

    /// <summary>Читает первый кадр (у значков — самый крупный) и поворачивает по EXIF.</summary>
    public static MagickImage Read(string path)
    {
        if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            using var icons = new MagickImageCollection(path);
            var largest = icons.OrderByDescending(i => i.Width * i.Height).First();
            return new MagickImage(largest);
        }
        var image = new MagickImage(path, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
        image.AutoOrient();
        return image;
    }

    private static bool IsAnimated(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext is not ("gif" or "webp"))
            return false;
        var info = MagickImageInfo.ReadCollection(path);
        return info.Count() > 1;
    }

    public static Task ConvertAsync(string source, string destination, string target, int quality, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (target is "gif" or "webp" && IsAnimated(source))
        {
            using var frames = new MagickImageCollection(source);
            frames.Coalesce();
            foreach (var frame in frames)
                frame.Quality = (uint)quality;
            frames.Write(destination, FormatOf(target));
            return;
        }

        using var image = Read(source);
        token.ThrowIfCancellationRequested();
        Prepare(image, target, quality);
        image.Write(destination, FormatOf(target));
    }, token);

    private static void Prepare(MagickImage image, string target, int quality)
    {
        switch (target)
        {
            case "jpg" or "bmp":
                // Без прозрачности: подкладываем белый фон, а не чёрный.
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaAction.Remove);
                break;
            case "ico":
                var side = Math.Min(256u, Math.Max(image.Width, image.Height));
                image.Resize(new MagickGeometry(side, side));
                image.BackgroundColor = MagickColors.Transparent;
                image.Extent(side, side, Gravity.Center);
                image.Settings.SetDefine(MagickFormat.Icon, "auto-resize", "256,128,64,48,32,24,16");
                break;
            case "pdf":
                image.Settings.Compression = CompressionMethod.JPEG;
                break;
        }
        image.Quality = (uint)quality;
    }

    /// <summary>Открывает, правит и сохраняет копию в подходящем формате.</summary>
    public static Task EditAsync(string source, string destination, string format, int quality, Action<MagickImage> edit, CancellationToken token) => Task.Run(() =>
    {
        using var image = Read(source);
        token.ThrowIfCancellationRequested();
        edit(image);
        Prepare(image, format, quality);
        image.Write(destination, FormatOf(format));
    }, token);

    /// <summary>
    /// Сжатие с потерями, но без заметной разницы: JPEG/WebP/AVIF перекодируются с качеством 72 и без метаданных,
    /// PNG сводится к 256-цветной палитре с дизерингом (как TinyPNG).
    /// </summary>
    public static Task CompressAsync(FileItem item, string destination, string format, CancellationToken token) => Task.Run(() =>
    {
        using var image = Read(item.Path);
        token.ThrowIfCancellationRequested();
        image.Strip();
        if (format == "png")
        {
            image.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
        }
        Prepare(image, format, format == "png" ? 95 : 72);
        image.Write(destination, FormatOf(format));
        if (format == Formats.Canonical(item.Extension) && new FileInfo(destination).Length >= new FileInfo(item.Path).Length)
            throw new InvalidOperationException("Файл уже сжат: меньше сделать не удалось.");
    }, token);

    public static void ResizeToMax(MagickImage image, uint max)
    {
        if (Math.Max(image.Width, image.Height) > max)
            image.Resize(new MagickGeometry(max, max));
    }

    public static void CropSquare(MagickImage image)
    {
        var side = Math.Min(image.Width, image.Height);
        image.Crop(side, side, Gravity.Center);
        image.ResetPage();
    }

    /// <summary>Несколько картинок — один PDF, по странице на картинку.</summary>
    public static Task ImagesToPdfAsync(IReadOnlyList<string> sources, string destination, int quality, IProgress<double> progress, CancellationToken token) => Task.Run(() =>
    {
        using var pages = new MagickImageCollection();
        for (var i = 0; i < sources.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var page = Read(sources[i]);
            page.BackgroundColor = MagickColors.White;
            page.Alpha(AlphaAction.Remove);
            page.Settings.Compression = CompressionMethod.JPEG;
            page.Quality = (uint)quality;
            pages.Add(page);
            progress.Report((i + 1.0) / (sources.Count + 1));
        }
        pages.Write(destination, MagickFormat.Pdf);
    }, token);

    /// <summary>PNG-байты для OCR и буфера обмена.</summary>
    public static byte[] ToPng(string path, uint maxSide)
    {
        using var image = Read(path);
        ResizeToMax(image, maxSide);
        return image.ToByteArray(MagickFormat.Png);
    }
}
