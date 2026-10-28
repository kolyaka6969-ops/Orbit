using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbit.Core.Backends;

/// <summary>
/// Сохранение проекта видеоредактора: клипы по порядку, каждый кадр рисуется по матрице клипа (приближение и ключи масштаба,
/// позиции, поворота), прозрачность затемняет кадр, громкость по ключам — звук. Декодирует и кодирует Media Foundation (Mf),
/// а рисует этот класс — тем же способом, что и просмотр в редакторе, поэтому результат совпадает с тем, что видно.
/// Здесь же — миниатюры и волна звука для шкалы.
/// </summary>
public static unsafe class VideoRenderer
{
    // ---------------------------------------------------------------- сохранение

    public static Task RenderAsync(VideoProject project, string destination, IProgress<double> progress, CancellationToken token) =>
        Task.Run(() => Render(project, destination, progress, token), token);

    private static void Render(VideoProject project, string destination, IProgress<double> progress, CancellationToken token)
    {
        var (width, height) = project.OutputSize;
        var fps = (double)project.FrameRateNumerator / project.FrameRateDenominator;
        var bitrate = (uint)Math.Clamp(width * height * Math.Clamp(fps, 10, 60) * project.BitsPerPixel, 300_000, 40_000_000);
        var withAudio = !project.Mute && project.Clips.Any(c => c.HasAudio);
        var total = Math.Max(1, project.Length.Ticks);
        var frameTicks = (long)(10_000_000 / Math.Clamp(fps, 1, 240));
        var frame = new uint[width * height];
        long offset = 0; // начало клипа в результате
        long audioFrames = 0; // сколько звука уже записано, в отсчётах 48 кГц

        using var writer = new Mf.Writer(destination, width, height, project.FrameRateNumerator, project.FrameRateDenominator, bitrate, withAudio);
        fixed (uint* output = frame)
        {
            foreach (var clip in project.Clips)
            {
                token.ThrowIfCancellationRequested();
                long start = clip.In.Ticks, end = clip.Out.Ticks;
                var scale = Matrix.Identity;
                scale.Scale((double)width / project.CanvasWidth, (double)height / project.CanvasHeight);
                using var reader = Mf.Reader.Open(clip.Path, true, withAudio && clip.HasAudio ? Mf.Writer.AudioRate : 0,
                    DecodeSize(clip, project.CanvasWidth, project.CanvasHeight, scale));
                if (start > 0)
                    reader.Seek(clip.In);
                bool videoDone = !reader.HasVideo, audioDone = !reader.HasAudio;
                long videoTime = 0, audioTime = 0;
                while (!videoDone || !audioDone)
                {
                    token.ThrowIfCancellationRequested();
                    if (!videoDone && (audioDone || videoTime <= audioTime))
                    {
                        if (!reader.Read(Mf.FirstVideoStream, out var sample))
                        {
                            videoDone = true;
                            continue;
                        }
                        using (sample)
                        {
                            videoTime = sample.Time;
                            var duration = sample.Duration > 0 ? sample.Duration : frameTicks;
                            if (sample.Time >= end)
                            {
                                videoDone = true;
                                reader.Deselect(Mf.FirstVideoStream);
                                continue;
                            }
                            if (sample.Time + duration / 2 < start)
                                continue;
                            var time = TimeSpan.FromTicks(sample.Time);
                            var matrix = clip.FrameMatrix(time, project.CanvasWidth, project.CanvasHeight) * scale;
                            Draw(reader, sample, clip, output, width, height, matrix, clip.Animation.Opacity.At(time).A);
                            writer.WriteVideo(output, offset + Math.Max(0, sample.Time - start), duration);
                            progress.Report(Math.Clamp((double)(offset + sample.Time - start) / total, 0, 1));
                        }
                    }
                    else
                    {
                        if (!reader.Read(Mf.FirstAudioStream, out var sample))
                        {
                            audioDone = true;
                            continue;
                        }
                        using (sample)
                        {
                            audioTime = sample.Time;
                            if (sample.Time >= end)
                            {
                                audioDone = true;
                                reader.Deselect(Mf.FirstAudioStream);
                                continue;
                            }
                            WriteAudio(writer, reader, sample, clip, offset, ref audioFrames);
                        }
                    }
                }
                offset += clip.Length.Ticks;
                if (withAudio)
                    WriteSilence(writer, offset * Mf.Writer.AudioRate / 10_000_000, ref audioFrames);
            }
        }
        writer.Finish();
        progress.Report(1);
    }

    /// <summary>Кусок звука клипа: обрезка по In/Out, громкость по ключам, стык с уже записанным без щелчков и дыр.</summary>
    private static void WriteAudio(Mf.Writer writer, Mf.Reader reader, Mf.Sample sample, VideoClip clip, long offset, ref long audioFrames)
    {
        const int rate = Mf.Writer.AudioRate;
        var pcm = ToStereo48(reader, sample);
        var count = pcm.Length / 2;
        long start = clip.In.Ticks, end = clip.Out.Ticks;
        // Отсчёты куска, попадающие в [In, Out)
        var first = (int)Math.Clamp((start - sample.Time) * rate / 10_000_000, 0, count);
        var last = (int)Math.Clamp((end - sample.Time) * rate / 10_000_000, 0, count);
        // Где этот кусок должен оказаться в результате
        var at = (offset + sample.Time - start) * rate / 10_000_000 + first;
        if (at > audioFrames + rate / 50)
            WriteSilence(writer, at, ref audioFrames); // дыра в звуке
        else if (at < audioFrames)
            first += (int)Math.Min(audioFrames - at, last - first); // наложение — пропускаем уже записанное
        if (last <= first)
            return;

        var volume = clip.Animation.Volume;
        var v0 = volume.At(TimeSpan.FromTicks(sample.Time + first * 10_000_000L / rate)).A;
        var v1 = volume.At(TimeSpan.FromTicks(sample.Time + last * 10_000_000L / rate)).A;
        if (Math.Abs(v0 - 1) > 1e-3 || Math.Abs(v1 - 1) > 1e-3)
        {
            for (var i = first; i < last; i++)
            {
                var v = v0 + (v1 - v0) * (i - first) / Math.Max(1, last - first);
                pcm[2 * i] = (short)Math.Clamp(pcm[2 * i] * v, short.MinValue, short.MaxValue);
                pcm[2 * i + 1] = (short)Math.Clamp(pcm[2 * i + 1] * v, short.MinValue, short.MaxValue);
            }
        }
        fixed (short* p = pcm)
            writer.WriteAudio(p + 2 * first, last - first, audioFrames * 10_000_000 / rate);
        audioFrames += last - first;
    }

    private static void WriteSilence(Mf.Writer writer, long until, ref long audioFrames)
    {
        var silence = new short[Mf.Writer.AudioRate / 10 * 2];
        fixed (short* quiet = silence)
        {
            while (audioFrames < until)
            {
                var count = (int)Math.Min(until - audioFrames, silence.Length / 2);
                writer.WriteAudio(quiet, count, audioFrames * 10_000_000 / Mf.Writer.AudioRate);
                audioFrames += count;
            }
        }
    }

    /// <summary>16-битный PCM файла → стерео 48 кГц. Обычно Media Foundation уже отдаёт его таким.</summary>
    private static short[] ToStereo48(Mf.Reader reader, Mf.Sample sample)
    {
        var source = (short*)sample.Data;
        var channels = Math.Max(1, reader.Channels);
        var frames = sample.Length / 2 / channels;
        if (channels == 2 && reader.SampleRate == Mf.Writer.AudioRate)
            return new ReadOnlySpan<short>(source, frames * 2).ToArray();
        var ratio = (double)reader.SampleRate / Mf.Writer.AudioRate;
        var count = (int)(frames / ratio);
        var result = new short[count * 2];
        for (var i = 0; i < count; i++)
        {
            var position = i * ratio;
            var j = Math.Min((int)position, frames - 1);
            var k = Math.Min(j + 1, frames - 1);
            var f = position - j;
            for (var c = 0; c < 2; c++)
            {
                var channel = Math.Min(c, channels - 1);
                result[2 * i + c] = (short)(source[j * channels + channel] * (1 - f) + source[k * channels + channel] * f);
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- рисование кадра

    /// <summary>
    /// Если клип во всём результате только уменьшается (4K в 720p), декодируем его сразу меньшего размера: так в разы быстрее
    /// и без ряби, которую даёт выборка из слишком большого кадра. Масштаб между ключами монотонен — хватает проверить ключи.
    /// </summary>
    private static (int Width, int Height)? DecodeSize(VideoClip clip, int canvasWidth, int canvasHeight, Matrix output)
    {
        var times = clip.Animation.All.SelectMany(v => v.Keys).Select(k => k.Time).Append(clip.In).Append(clip.Out);
        var factor = times.Max(t =>
        {
            var m = clip.FrameMatrix(t, canvasWidth, canvasHeight) * output;
            return Math.Sqrt(Math.Abs(m.Determinant));
        });
        if (factor > 0.7)
            return null;
        var width = Math.Max(16, (int)Math.Ceiling(clip.Width * factor) & ~1);
        var height = Math.Max(16, (int)Math.Ceiling(clip.Height * factor) & ~1);
        return (width, height);
    }

    private static void Draw(Mf.Reader reader, Mf.Sample sample, VideoClip clip, uint* output, int width, int height, Matrix matrix, double opacity)
    {
        // Кадр бывает выше видимой части (1088 строк у 1080p) — лишнее снизу не берём
        var sourceWidth = Math.Min(reader.Width, (int)clip.Width);
        var sourceHeight = Math.Min(reader.Height, (int)clip.Height);
        if (reader.Scaled)
        {
            // Уменьшенный при декодировании кадр: его пиксели крупнее пикселей файла
            (sourceWidth, sourceHeight) = (reader.Width, reader.Height);
            var pre = Matrix.Identity;
            pre.Scale((double)clip.Width / reader.Width, (double)clip.Height / reader.Height);
            matrix = pre * matrix;
        }
        var stride = reader.Stride;
        var top = stride >= 0 ? sample.Data : sample.Data + (long)(-stride) * (reader.Height - 1);
        if (sample.Length < Math.Abs(stride) * sourceHeight)
            throw new InvalidOperationException("Кадр меньше ожидаемого.");
        Draw((uint*)top, sourceWidth, sourceHeight, stride / 4, output, width, height, matrix, opacity);
    }

    /// <summary>
    /// Рисует кадр source в output по матрице (пиксели источника → пиксели результата) с билинейной выборкой;
    /// вне кадра — чёрное, края при повороте сглажены. Opacity смешивает с чёрным.
    /// </summary>
    private static void Draw(uint* source, int sourceWidth, int sourceHeight, int sourceStride, uint* output, int width, int height, Matrix matrix, double opacity)
    {
        var fade = (uint)Math.Round(Math.Clamp(opacity, 0, 1) * 256);
        if (!matrix.HasInverse || fade == 0)
        {
            new Span<uint>(output, width * height).Fill(0xFF000000);
            return;
        }
        var inverse = matrix;
        inverse.Invert();
        // Тот же размер без сдвигов — просто копия
        if (sourceWidth == width && sourceHeight == height && Near(matrix, Matrix.Identity))
        {
            var src = (nint)source;
            var dst = (nint)output;
            Parallel.For(0, height, y =>
            {
                var from = (uint*)src + (long)y * sourceStride;
                var to = (uint*)dst + (long)y * width;
                for (var x = 0; x < width; x++)
                    to[x] = (fade >= 256 ? from[x] : Lerp(0, from[x], fade)) | 0xFF000000;
            });
            return;
        }
        var source0 = (nint)source;
        var output0 = (nint)output;
        Parallel.For(0, height, y =>
        {
            var src = (uint*)source0;
            var row = (uint*)output0 + (long)y * width;
            var py = y + 0.5;
            // Центр пикселя результата → координаты в источнике; вдоль строки они меняются на постоянный шаг
            var sx = inverse.M11 * 0.5 + inverse.M21 * py + inverse.OffsetX - 0.5;
            var sy = inverse.M12 * 0.5 + inverse.M22 * py + inverse.OffsetY - 0.5;
            for (var x = 0; x < width; x++, sx += inverse.M11, sy += inverse.M12)
            {
                if (sx <= -1 || sy <= -1 || sx >= sourceWidth || sy >= sourceHeight)
                {
                    row[x] = 0xFF000000;
                    continue;
                }
                var ix = (int)Math.Floor(sx);
                var iy = (int)Math.Floor(sy);
                var fx = (uint)((sx - ix) * 256);
                var fy = (uint)((sy - iy) * 256);
                uint p00 = Pixel(src, sourceWidth, sourceHeight, sourceStride, ix, iy), p10 = Pixel(src, sourceWidth, sourceHeight, sourceStride, ix + 1, iy);
                uint p01 = Pixel(src, sourceWidth, sourceHeight, sourceStride, ix, iy + 1), p11 = Pixel(src, sourceWidth, sourceHeight, sourceStride, ix + 1, iy + 1);
                var color = Lerp(Lerp(p00, p10, fx), Lerp(p01, p11, fx), fy);
                row[x] = (fade >= 256 ? color : Lerp(0, color, fade)) | 0xFF000000;
            }
        });
    }

    private static uint Pixel(uint* source, int width, int height, int stride, int x, int y) =>
        (uint)x < (uint)width && (uint)y < (uint)height ? source[(long)y * stride + x] | 0xFF000000 : 0;

    /// <summary>Смешивание двух BGRA-пикселей, f от 0 (a) до 256 (b), два канала за раз.</summary>
    private static uint Lerp(uint a, uint b, uint f)
    {
        var g = 256 - f;
        var rb = (((a & 0x00FF00FF) * g + (b & 0x00FF00FF) * f) >> 8) & 0x00FF00FF;
        var ag = ((((a >> 8) & 0x00FF00FF) * g + ((b >> 8) & 0x00FF00FF) * f) >> 8) & 0x00FF00FF;
        return rb | (ag << 8);
    }

    private static bool Near(Matrix a, Matrix b) =>
        Math.Abs(a.M11 - b.M11) < 1e-6 && Math.Abs(a.M12 - b.M12) < 1e-6 && Math.Abs(a.M21 - b.M21) < 1e-6 &&
        Math.Abs(a.M22 - b.M22) < 1e-6 && Math.Abs(a.OffsetX - b.OffsetX) < 1e-3 && Math.Abs(a.OffsetY - b.OffsetY) < 1e-3;

    // ---------------------------------------------------------------- кадр в PNG

    /// <summary>Кадр проекта в момент time клипа (время файла) — как в просмотре, размером с кадр проекта, но не больше 4K.</summary>
    public static Task<BitmapSource> FrameAsync(VideoClip clip, TimeSpan time, int canvasWidth, int canvasHeight, CancellationToken token) => Task.Run(() =>
    {
        var fit = Math.Min(1, 3840.0 / Math.Max(canvasWidth, canvasHeight));
        int width = (int)Math.Round(canvasWidth * fit), height = (int)Math.Round(canvasHeight * fit);
        using var reader = Mf.Reader.Open(clip.Path, true, 0);
        if (!reader.HasVideo)
            throw new InvalidOperationException("В файле нет видеодорожки.");
        reader.Seek(time);
        var pixels = new uint[width * height];
        var found = false;
        // От ключевого кадра до нужного — по одному, пока не дойдём
        while (reader.Read(Mf.FirstVideoStream, out var sample))
        {
            token.ThrowIfCancellationRequested();
            using (sample)
            {
                var last = sample.Time + Math.Max(sample.Duration, 1) > time.Ticks;
                if (!last && found)
                    continue;
                var matrix = clip.FrameMatrix(TimeSpan.FromTicks(Math.Max(sample.Time, time.Ticks)), canvasWidth, canvasHeight);
                matrix.Scale(fit, fit);
                fixed (uint* output = pixels)
                    Draw(reader, sample, clip, output, width, height, matrix, clip.Animation.Opacity.At(time).A);
                found = true;
                if (last)
                    break;
            }
        }
        if (!found)
            throw new InvalidOperationException("Не удалось прочитать кадр.");
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }, token);

    // ---------------------------------------------------------------- миниатюры и волна для шкалы

    /// <summary>Миниатюры высотой height через равные промежутки — для полосы кадров на дорожке.</summary>
    public static Task<IReadOnlyList<(TimeSpan Time, BitmapSource Image)>> ThumbnailsAsync(string path, TimeSpan duration, int height, CancellationToken token) =>
        Task.Run<IReadOnlyList<(TimeSpan, BitmapSource)>>(() =>
        {
            var result = new List<(TimeSpan, BitmapSource)>();
            using var reader = Mf.Reader.Open(path, true, 0);
            if (!reader.HasVideo)
                return result;
            var count = (int)Math.Clamp(duration.TotalSeconds / 1.5, 2, 48);
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var target = duration * ((i + 0.5) / count);
                reader.Seek(target);
                // Ближайший кадр не раньше нужного, но не дальше пары секунд декодирования
                for (var skipped = 0; reader.Read(Mf.FirstVideoStream, out var sample); skipped++)
                {
                    using (sample)
                    {
                        if (sample.Time < target.Ticks - 400_000 && skipped < 90)
                            continue;
                        result.Add((TimeSpan.FromTicks(sample.Time), Shrink(reader, sample, height)));
                        break;
                    }
                }
            }
            return result;
        }, token);

    private static BitmapSource Shrink(Mf.Reader reader, Mf.Sample sample, int height)
    {
        int sw = reader.Width, sh = reader.Height, stride = reader.Stride / 4;
        var top = (uint*)(stride >= 0 ? sample.Data : sample.Data + (long)(-reader.Stride) * (sh - 1));
        var width = Math.Max(1, (int)Math.Round((double)sw / sh * height));
        var pixels = new uint[width * height];
        for (var y = 0; y < height; y++)
        {
            int y0 = y * sh / height, y1 = Math.Max(y0 + 1, (y + 1) * sh / height);
            for (var x = 0; x < width; x++)
            {
                int x0 = x * sw / width, x1 = Math.Max(x0 + 1, (x + 1) * sw / width);
                // Среднее по прямоугольнику, через пиксель — для миниатюры этого хватает
                uint b = 0, g = 0, r = 0, n = 0;
                for (var yy = y0; yy < y1; yy += 2)
                    for (var xx = x0; xx < x1; xx += 2)
                    {
                        var p = top[(long)yy * stride + xx];
                        b += p & 0xFF;
                        g += (p >> 8) & 0xFF;
                        r += (p >> 16) & 0xFF;
                        n++;
                    }
                pixels[y * width + x] = 0xFF000000 | (r / n << 16) | (g / n << 8) | (b / n);
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Громкость звука по кусочкам (perSecond в секунду), 0…1; пусто — звука нет.</summary>
    public static Task<float[]> WaveformAsync(string path, int perSecond, CancellationToken token) => Task.Run(() =>
    {
        using var reader = Mf.Reader.Open(path, false, 8000);
        if (!reader.HasAudio)
            return [];
        var peaks = new List<float>();
        while (reader.Read(Mf.FirstAudioStream, out var sample))
        {
            token.ThrowIfCancellationRequested();
            using (sample)
            {
                var data = (short*)sample.Data;
                var channels = Math.Max(1, reader.Channels);
                var frames = sample.Length / 2 / channels;
                for (var i = 0; i < frames; i++)
                {
                    var bucket = (int)((sample.Time + i * 10_000_000L / reader.SampleRate) * perSecond / 10_000_000);
                    if (bucket < 0)
                        continue;
                    while (peaks.Count <= bucket)
                        peaks.Add(0);
                    for (var c = 0; c < channels; c++)
                        peaks[bucket] = Math.Max(peaks[bucket], Math.Abs(data[i * channels + c] / 32768f));
                }
            }
        }
        return peaks.ToArray();
    }, token);
}
