using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Orbit.Core.Backends;

/// <summary>Распознавание текста встроенным в Windows OCR (языки — те, что установлены в системе).</summary>
public static class OcrBackend
{
    public static async Task<string> RecognizeAsync(string path, CancellationToken token)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("В Windows не установлены языки для распознавания текста.");
        var png = await Task.Run(() => ImageBackend.ToPng(path, OcrEngine.MaxImageDimension), token);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        token.ThrowIfCancellationRequested();
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }
}
