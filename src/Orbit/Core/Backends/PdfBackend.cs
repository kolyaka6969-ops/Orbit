using System.Runtime.InteropServices.WindowsRuntime;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using WinPdf = Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Orbit.Core.Backends;

/// <summary>PDF: страницы в картинки (встроенный в Windows движок PDF), объединение и разделение (PDFsharp).</summary>
public static class PdfBackend
{
    /// <summary>Рендерит страницы. Одна страница — один файл рядом, много страниц — папка «Имя (страницы)».</summary>
    public static async Task<IReadOnlyList<string>> ToImagesAsync(FileItem item, string outDir, string format, int dpi, IProgress<double> progress, CancellationToken token)
    {
        var file = await StorageFile.GetFileFromPathAsync(item.Path);
        WinPdf.PdfDocument document;
        try
        {
            document = await WinPdf.PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B))
        {
            throw new InvalidOperationException("PDF защищён паролем.");
        }

        var count = document.PageCount;
        var dir = outDir;
        if (count > 1)
        {
            dir = OutputPaths.UniqueFolder(outDir, $"{item.BaseName} (страницы)");
            Directory.CreateDirectory(dir);
        }
        var digits = count.ToString().Length;
        var results = new List<string>();
        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            using var page = document.GetPage(i);
            var options = new WinPdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Round(page.Size.Width * dpi / 96.0),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                BitmapEncoderId = format == "jpg" ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId,
            };
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options);
            var name = count > 1 ? $"{item.BaseName} — {(i + 1).ToString().PadLeft(digits, '0')}" : item.BaseName;
            results.Add(await OutputPaths.ProduceAsync(dir, name, format, async temp =>
            {
                stream.Seek(0);
                await using var target = File.Create(temp);
                await stream.AsStreamForRead().CopyToAsync(target, token);
            }));
            progress.Report((i + 1.0) / count);
        }
        return count > 1 ? [dir] : results;
    }

    public static Task MergeAsync(IReadOnlyList<string> sources, string destination, IProgress<double> progress, CancellationToken token) => Task.Run(() =>
    {
        using var output = new PdfSharp.Pdf.PdfDocument();
        for (var i = 0; i < sources.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            using var input = Open(sources[i]);
            foreach (var page in input.Pages)
                output.AddPage(page);
            progress.Report((i + 1.0) / (sources.Count + 1));
        }
        output.Save(destination);
    }, token);

    /// <summary>Каждая страница — отдельный PDF в папке «Имя (страницы)».</summary>
    public static Task<IReadOnlyList<string>> SplitAsync(FileItem item, string outDir, IProgress<double> progress, CancellationToken token) => Task.Run<IReadOnlyList<string>>(() =>
    {
        using var input = Open(item.Path);
        var count = input.PageCount;
        if (count < 2)
            throw new InvalidOperationException("В документе одна страница — делить нечего.");
        var dir = OutputPaths.UniqueFolder(outDir, $"{item.BaseName} (страницы)");
        Directory.CreateDirectory(dir);
        var digits = count.ToString().Length;
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            using var single = new PdfSharp.Pdf.PdfDocument();
            single.AddPage(input.Pages[i]);
            single.Save(Path.Combine(dir, $"{item.BaseName} — {(i + 1).ToString().PadLeft(digits, '0')}.pdf"));
            progress.Report((i + 1.0) / count);
        }
        return [dir];
    }, token);

    private static PdfSharp.Pdf.PdfDocument Open(string path)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Import);
        }
        catch (PdfReaderException ex)
        {
            throw new InvalidOperationException($"Не удалось открыть {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
