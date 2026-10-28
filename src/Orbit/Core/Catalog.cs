using ImageMagick;
using Orbit.Core.Backends;
using Orbit.Shell;
using Windows.Media.MediaProperties;

namespace Orbit.Core;

/// <summary>Символы шрифта Segoe Fluent Icons.</summary>
public static class Glyphs
{
    public const string Photo = "";
    public const string Audio = "";
    public const string Video = "";
    public const string Document = "";
    public const string Pdf = "";
    public const string Zip = "";
    public const string Extract = "";
    public const string Shrink = "";
    public const string RotateRight = "";
    public const string RotateLeft = "";
    public const string Mirror = "";
    public const string Color = "";
    public const string Clear = "";
    public const string Edit = "";
    public const string Crop = "";
    public const string Text = "";
    public const string Merge = "";
    public const string Split = "";
    public const string Mute = "";
    public const string Hash = "";
    public const string Link = "";
    public const string Paste = "";
    public const string Desktop = "";
    public const string Move = "";
    public const string Copy = "";
    public const string Delete = "";
    public const string More = "";
    public const string Settings = "";
    public const string Info = "";
    public const string Keyboard = "";
    public const string Save = "";
    public const string Power = "";
    public const string Pause = "";
    public const string Ringer = "";
    public const string Folder = "";
    public const string Close = "";
    public const string Check = "";
    public const string Error = "";
    public const string Mouse = "";
    public const string ContextMenu = "";
    public const string Exit = "";
}

/// <summary>Все пункты трёх круговых меню. Порядок — по часовой стрелке от «12 часов».</summary>
public static class Catalog
{
    public static IReadOnlyList<Operation> Build()
    {
        var ops = new List<Operation>();
        AddImages(ops);
        AddMedia(ops);
        AddPdf(ops);
        AddOffice(ops);
        AddFiles(ops);
        AddActions(ops);
        return ops;
    }

    // ---------------------------------------------------------------- изображения

    private static void AddImages(List<Operation> ops)
    {
        foreach (var (target, subtitle) in ImageBackend.Targets)
        {
            ops.Add(PerFile($"image.to.{target}", WheelKind.Convert, target.ToUpperInvariant(), Glyphs.Photo,
                item => Formats.IsPicture(item) && Formats.Canonical(item.Extension) != target,
                async (item, ctx, _) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item), item.BaseName, target,
                    tmp => ImageBackend.ConvertAsync(item.Path, tmp, target, ctx.Settings.ImageQuality, ctx.Token))],
                subtitle));
        }

        // Первым в круге инструментов — на «12 часах»
        ops.Add(new Operation
        {
            Id = "image.editor",
            Wheel = WheelKind.Tools,
            Label = "Редактор",
            Subtitle = "рисовать",
            Glyph = Glyphs.Edit,
            Instant = true,
            Interactive = true,
            // Несколько картинок открываются уже поставленными в ряд
            AppliesTo = items => items.Count is >= 1 and <= 4 && items.All(Formats.IsPicture),
            Run = UI.Editor.ImageEditorWindow.EditAsync,
        });

        ops.Add(PerFile("image.compress", WheelKind.Tools, "Сжать", Glyphs.Shrink, Formats.IsPicture,
            async (item, ctx, _) =>
            {
                var format = ImageBackend.EditFormatFor(item);
                return [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item), $"{item.BaseName} (сжато)", format,
                    tmp => ImageBackend.CompressAsync(item, tmp, format, ctx.Token))];
            }, "меньше вес"));
        ImageEdit(ops, "image.half", "Уменьшить", Glyphs.Shrink, "50 %", "50%", img => img.Resize(new Percentage(50)));
        ImageEdit(ops, "image.web", "Для интернета", Glyphs.Shrink, "до 1920 px", "1920", img => ImageBackend.ResizeToMax(img, 1920));
        ImageEdit(ops, "image.rotate.right", "Повернуть", Glyphs.RotateRight, "вправо", "повёрнуто", img => img.Rotate(90));
        ImageEdit(ops, "image.rotate.left", "Повернуть", Glyphs.RotateLeft, "влево", "повёрнуто", img => img.Rotate(-90));
        ImageEdit(ops, "image.mirror", "Отразить", Glyphs.Mirror, "зеркально", "отражено", img => img.Flop());
        ImageEdit(ops, "image.gray", "Ч/б", Glyphs.Color, "серый", "ч-б", img => img.Grayscale());
        ImageEdit(ops, "image.square", "Квадрат", Glyphs.Crop, "обрезка", "квадрат", ImageBackend.CropSquare);
        ImageEdit(ops, "image.strip", "Без метаданных", Glyphs.Clear, "EXIF, GPS", "без метаданных", img => img.Strip());

        ops.Add(PerFile("image.ocr", WheelKind.Tools, "Текст", Glyphs.Text, Formats.IsPicture,
            async (item, ctx, _) =>
            {
                var text = await OcrBackend.RecognizeAsync(item.Path, ctx.Token);
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException("Текст на изображении не найден.");
                UiServices.SetClipboardText(text);
                ctx.Message = "Текст скопирован в буфер обмена";
                return [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item), item.BaseName, "txt",
                    tmp => File.WriteAllTextAsync(tmp, text, ctx.Token))];
            }, "распознать"));

        ops.Add(new Operation
        {
            Id = "image.pdf",
            Wheel = WheelKind.Tools,
            Label = "В один PDF",
            Subtitle = "по странице",
            Glyph = Glyphs.Pdf,
            AppliesTo = items => items.Count >= 2 && items.All(Formats.IsPicture),
            Run = async (items, ctx) =>
            {
                var sorted = items.OrderBy(i => i.Name, NaturalComparer.Instance).ToList();
                return [await OutputPaths.ProduceAsync(ctx.OutputDirFor(sorted[0]), sorted[0].BaseName, "pdf",
                    tmp => ImageBackend.ImagesToPdfAsync(sorted.Select(i => i.Path).ToList(), tmp, ctx.Settings.ImageQuality, ctx.Progress, ctx.Token))];
            },
        });
    }

    private static void ImageEdit(List<Operation> ops, string id, string label, string glyph, string subtitle, string suffix, Action<MagickImage> edit) =>
        ops.Add(PerFile(id, WheelKind.Tools, label, glyph, Formats.IsPicture,
            async (item, ctx, _) =>
            {
                var format = ImageBackend.EditFormatFor(item);
                return [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item), $"{item.BaseName} ({suffix})", format,
                    tmp => ImageBackend.EditAsync(item.Path, tmp, format, ctx.Settings.ImageQuality, edit, ctx.Token))];
            }, subtitle));

    // ---------------------------------------------------------------- аудио и видео

    private static void AddMedia(List<Operation> ops)
    {
        foreach (var (target, subtitle) in MediaBackend.AudioTargets)
        {
            ops.Add(PerFile($"audio.to.{target}", WheelKind.Convert, target.ToUpperInvariant(), Glyphs.Audio,
                item => item.Kind == FileKind.Audio && Formats.Canonical(item.Extension) != target,
                async (item, ctx, p) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item), item.BaseName, target,
                    tmp => MediaBackend.ToAudioAsync(item.Path, tmp, target, AudioEncodingQuality.High, p, ctx.Token))],
                subtitle));
        }
        ops.Add(PerFile("audio.compress", WheelKind.Tools, "Сжать", Glyphs.Shrink, item => item.Kind == FileKind.Audio,
            async (item, ctx, p) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item), $"{item.BaseName} (сжато)", "mp3",
                tmp => MediaBackend.ToAudioAsync(item.Path, tmp, "mp3", AudioEncodingQuality.Medium, p, ctx.Token))],
            "MP3 128 кбит/с"));

        foreach (var (target, subtitle) in MediaBackend.VideoTargets)
        {
            ops.Add(PerFile($"video.to.{target}", WheelKind.Convert, target.ToUpperInvariant(), Glyphs.Video,
                item => item.Kind == FileKind.Video && Formats.Canonical(item.Extension) != target,
                async (item, ctx, p) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item), item.BaseName, target,
                    tmp => MediaBackend.ToVideoAsync(item.Path, tmp, target, new(BitsPerPixel: MediaBackend.BitsPerPixel(ctx.Settings.Video)), p, ctx.Token))],
                subtitle));
        }
        foreach (var target in new[] { "mp3", "m4a" })
        {
            ops.Add(PerFile($"video.audio.{target}", WheelKind.Convert, target.ToUpperInvariant(), Glyphs.Audio,
                item => item.Kind == FileKind.Video,
                async (item, ctx, p) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item), item.BaseName, target,
                    tmp => MediaBackend.ToAudioAsync(item.Path, tmp, target, AudioEncodingQuality.High, p, ctx.Token))],
                "только звук"));
        }

        // Первым в круге инструментов для видео
        ops.Add(new Operation
        {
            Id = "video.editor",
            Wheel = WheelKind.Tools,
            Label = "Редактор",
            Subtitle = "обрезать",
            Glyph = Glyphs.Edit,
            Instant = true,
            Interactive = true,
            AppliesTo = items => items.Count == 1 && items[0].Kind == FileKind.Video,
            Run = UI.Editor.VideoEditorWindow.EditAsync,
        });
        Video(ops, "video.compress", "Сжать", Glyphs.Shrink, "H.264", "сжато", _ => new(BitsPerPixel: 0.045));
        Video(ops, "video.720", "720p", Glyphs.Shrink, "уменьшить", "720p", ctx => new(MaxHeight: 720, BitsPerPixel: MediaBackend.BitsPerPixel(ctx.Settings.Video)));
        Video(ops, "video.mute", "Без звука", Glyphs.Mute, "убрать дорожку", "без звука", ctx => new(BitsPerPixel: MediaBackend.BitsPerPixel(ctx.Settings.Video), Mute: true));
    }

    private static void Video(List<Operation> ops, string id, string label, string glyph, string subtitle, string suffix, Func<JobContext, MediaBackend.VideoOptions> options) =>
        ops.Add(PerFile(id, WheelKind.Tools, label, glyph, item => item.Kind == FileKind.Video,
            async (item, ctx, p) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item), $"{item.BaseName} ({suffix})", "mp4",
                tmp => MediaBackend.ToVideoAsync(item.Path, tmp, "mp4", options(ctx), p, ctx.Token))],
            subtitle));

    // ---------------------------------------------------------------- PDF

    private static void AddPdf(List<Operation> ops)
    {
        foreach (var target in new[] { "png", "jpg" })
        {
            ops.Add(PerFile($"pdf.to.{target}", WheelKind.Convert, target.ToUpperInvariant(), Glyphs.Photo,
                item => item.Kind == FileKind.Pdf,
                (item, ctx, p) => PdfBackend.ToImagesAsync(item, ctx.OutputDirFor(item), target, ctx.Settings.PdfDpi, p, ctx.Token),
                "страницы"));
        }
        ops.Add(PerFile("pdf.to.docx", WheelKind.Convert, "DOCX", Glyphs.Document, item => item.Kind == FileKind.Pdf,
            async (item, ctx, p) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item), item.BaseName, "docx",
                tmp => OfficeBackend.PdfToWordAsync(item.Path, tmp, p, ctx.Token))],
            "через Word"));
        ops.Add(new Operation
        {
            Id = "pdf.merge",
            Wheel = WheelKind.Tools,
            Label = "Объединить",
            Subtitle = "в один PDF",
            Glyph = Glyphs.Merge,
            AppliesTo = items => items.Count >= 2 && items.All(i => i.Kind == FileKind.Pdf),
            Run = async (items, ctx) =>
            {
                var sorted = items.OrderBy(i => i.Name, NaturalComparer.Instance).ToList();
                return [await OutputPaths.ProduceAsync(ctx.OutputDirFor(sorted[0]), $"{sorted[0].BaseName} (объединено)", "pdf",
                    tmp => PdfBackend.MergeAsync(sorted.Select(i => i.Path).ToList(), tmp, ctx.Progress, ctx.Token))];
            },
        });
        ops.Add(PerFile("pdf.split", WheelKind.Tools, "Разделить", Glyphs.Split, item => item.Kind == FileKind.Pdf,
            (item, ctx, p) => PdfBackend.SplitAsync(item, ctx.OutputDirFor(item), p, ctx.Token), "по страницам"));
    }

    // ---------------------------------------------------------------- Word и Excel

    private static void AddOffice(List<Operation> ops)
    {
        foreach (var (target, subtitle) in OfficeBackend.DocumentTargets)
        {
            ops.Add(PerFile($"document.to.{target}", WheelKind.Convert, target.ToUpperInvariant(), target == "pdf" ? Glyphs.Pdf : Glyphs.Document,
                item => item.Kind == FileKind.Document && item.Extension != target,
                async (item, ctx, p) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item), item.BaseName, target,
                    tmp => OfficeBackend.DocumentAsync(item.Path, tmp, target, ctx.Settings.Office, p, ctx.Token))],
                subtitle));
        }
        foreach (var (target, subtitle) in OfficeBackend.SpreadsheetTargets)
        {
            ops.Add(PerFile($"spreadsheet.to.{target}", WheelKind.Convert, target.ToUpperInvariant(), target == "pdf" ? Glyphs.Pdf : Glyphs.Document,
                item => item.Kind == FileKind.Spreadsheet && item.Extension != target,
                async (item, ctx, p) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item), item.BaseName, target,
                    tmp => OfficeBackend.SpreadsheetAsync(item.Path, tmp, target, ctx.Settings.Office, p, ctx.Token))],
                subtitle));
        }
    }

    // ---------------------------------------------------------------- архивы и любые файлы

    private static void AddFiles(List<Operation> ops)
    {
        ops.Add(PerFile("archive.extract", WheelKind.Convert, "Распаковать", Glyphs.Extract, item => item.Kind == FileKind.Archive,
            async (item, ctx, p) => [await ArchiveBackend.ExtractAsync(item, ctx.OutputDirFor(item), p, ctx.Token)], "в папку"));

        Func<IReadOnlyList<FileItem>, JobContext, Task<IReadOnlyList<string>>> zip = async (items, ctx) =>
        {
            var name = items.Count == 1 ? items[0].BaseName : new DirectoryInfo(items[0].Folder).Name is { Length: > 0 } folder && !folder.EndsWith(':') ? folder : "Архив";
            return [await OutputPaths.ProduceAsync(ctx.OutputDirFor(items[0]), name, "zip", tmp => ArchiveBackend.ZipAsync(items, tmp, ctx.Progress, ctx.Token))];
        };
        // На колесе конвертации ZIP есть для того, что больше ни во что не конвертируется (папки, прочие файлы).
        ops.Add(new Operation
        {
            Id = "convert.zip",
            Wheel = WheelKind.Convert,
            Label = "ZIP",
            Subtitle = "архив",
            Glyph = Glyphs.Zip,
            AppliesTo = items => items.Count > 0 && items.All(i => i.Kind is FileKind.Folder or FileKind.Other),
            Run = zip,
        });
        ops.Add(new Operation
        {
            Id = "tools.zip",
            Wheel = WheelKind.Tools,
            Label = "ZIP",
            Subtitle = "архив",
            Glyph = Glyphs.Zip,
            AppliesTo = items => items.Count > 0,
            Run = zip,
        });
        ops.Add(new Operation
        {
            Id = "tools.sha256",
            Wheel = WheelKind.Tools,
            Label = "SHA-256",
            Subtitle = "контр. сумма",
            Glyph = Glyphs.Hash,
            AppliesTo = items => items.Count > 0 && items.All(i => !i.IsDirectory),
            Run = async (items, ctx) =>
            {
                var lines = new List<string>();
                foreach (var item in items)
                {
                    await using var stream = File.OpenRead(item.Path);
                    var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ctx.Token);
                    var hex = Convert.ToHexStringLower(hash);
                    lines.Add(items.Count == 1 ? hex : $"{hex}  {item.Name}");
                }
                UiServices.SetClipboardText(string.Join(Environment.NewLine, lines));
                ctx.Message = "Контрольная сумма скопирована";
                return [];
            },
        });
    }

    // ---------------------------------------------------------------- действия (Alt)

    private static void AddActions(List<Operation> ops)
    {
        ops.Add(Action("action.copy.path", "Путь", Glyphs.Link, "копировать", (items, ctx) =>
        {
            UiServices.SetClipboardText(string.Join(Environment.NewLine, items.Select(i => i.Path)));
            ctx.Message = items.Count == 1 ? "Путь скопирован" : "Пути скопированы";
            return [];
        }));
        ops.Add(Action("action.copy.clipboard", "В буфер", Glyphs.Paste, "как файлы", (items, ctx) =>
        {
            UiServices.SetClipboardFiles(items.Select(i => i.Path));
            ctx.Message = "Файлы в буфере обмена — вставьте их через Ctrl+V";
            return [];
        }));
        ops.Add(Action("action.desktop", "На рабочий стол", Glyphs.Desktop, "копия", (items, ctx) =>
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return items.Select(i => CopyItem(i, desktop, i.IsDirectory ? i.Name : i.BaseName)).ToList();
        }, instant: false));
        ops.Add(Action("action.move", "Переместить", Glyphs.Move, "в папку…", (items, ctx) =>
        {
            var target = UiServices.PickFolder("Куда переместить файлы?");
            if (target is null)
                throw new OperationCanceledException();
            var moved = new List<string>();
            foreach (var item in items)
            {
                var destination = OutputPaths.Unique(target, item.IsDirectory ? item.Name : item.BaseName, item.IsDirectory ? "" : item.Extension);
                if (item.IsDirectory)
                    Directory.Move(item.Path, destination);
                else
                    File.Move(item.Path, destination);
                moved.Add(destination);
            }
            return moved;
        }, instant: false));
        ops.Add(Action("action.duplicate", "Копия", Glyphs.Copy, "рядом", (items, ctx) =>
            items.Select(i => CopyItem(i, i.Folder, $"{(i.IsDirectory ? i.Name : i.BaseName)} (копия)")).ToList(), instant: false));
        ops.Add(Action("action.recycle", "В корзину", Glyphs.Delete, "можно вернуть", (items, ctx) =>
        {
            Native.Recycle(items.Select(i => i.Path));
            ctx.Message = items.Count == 1 ? "Перемещено в корзину" : $"В корзине: {items.Count}";
            return [];
        }));
    }

    private static Operation Action(string id, string label, string glyph, string subtitle, Func<IReadOnlyList<FileItem>, JobContext, IReadOnlyList<string>> run, bool instant = true) => new()
    {
        Id = id,
        Wheel = WheelKind.Actions,
        Label = label,
        Subtitle = subtitle,
        Glyph = glyph,
        Instant = instant,
        AppliesTo = items => items.Count > 0,
        Run = (items, ctx) => Task.Run(() => run(items, ctx), ctx.Token),
    };

    private static string CopyItem(FileItem item, string targetDir, string baseName)
    {
        var destination = OutputPaths.Unique(targetDir, baseName, item.IsDirectory ? "" : item.Extension);
        if (item.IsDirectory)
            CopyDirectory(item.Path, destination);
        else
            File.Copy(item.Path, destination);
        return destination;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    // ---------------------------------------------------------------- помощники

    /// <summary>Операция, которая выполняется для каждого файла по очереди.</summary>
    private static Operation PerFile(string id, WheelKind wheel, string label, string glyph, Func<FileItem, bool> accepts,
        Func<FileItem, JobContext, IProgress<double>, Task<IReadOnlyList<string>>> run, string? subtitle = null) => new()
    {
        Id = id,
        Wheel = wheel,
        Label = label,
        Subtitle = subtitle,
        Glyph = glyph,
        AppliesTo = items => items.Count > 0 && items.All(accepts),
        Run = async (items, ctx) =>
        {
            var outputs = new List<string>();
            for (var i = 0; i < items.Count; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var part = new PartProgress(ctx.Progress, i, items.Count);
                outputs.AddRange(await run(items[i], ctx, part));
                part.Report(1);
            }
            return outputs;
        },
    };

    /// <summary>Прогресс одного файла из нескольких.</summary>
    private sealed class PartProgress(IProgress<double> total, int index, int count) : IProgress<double>
    {
        public void Report(double value) => total.Report((index + Math.Clamp(value, 0, 1)) / count);
    }
}

/// <summary>Сортирует «2.jpg» перед «10.jpg», как Проводник.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
