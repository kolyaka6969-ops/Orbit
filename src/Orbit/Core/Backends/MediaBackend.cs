using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Orbit.Core.Backends;

/// <summary>
/// Аудио и видео через Media Foundation — кодеки, которые уже есть в Windows. Ничего скачивать не нужно:
/// MP3, AAC (M4A), WAV, FLAC, WMA, H.264 (MP4) и WMV.
/// </summary>
public static class MediaBackend
{
    public static readonly (string Ext, string? Subtitle)[] AudioTargets =
    [
        ("mp3", null),
        ("m4a", "AAC"),
        ("wav", "без сжатия"),
        ("flac", "без потерь"),
        ("wma", null),
    ];

    /// <summary>Видео → видео: (расширение, подпись).</summary>
    public static readonly (string Ext, string? Subtitle)[] VideoTargets =
    [
        ("mp4", "H.264"),
        ("wmv", null),
    ];

    public sealed record VideoOptions(uint MaxHeight = 0, double BitsPerPixel = 0.08, bool Mute = false);

    public static double BitsPerPixel(VideoQuality quality) => quality switch
    {
        VideoQuality.Small => 0.05,
        VideoQuality.High => 0.13,
        _ => 0.08,
    };

    public static async Task ToAudioAsync(string source, string destination, string target, AudioEncodingQuality quality, IProgress<double> progress, CancellationToken token)
    {
        var profile = target switch
        {
            "mp3" => MediaEncodingProfile.CreateMp3(quality),
            "m4a" => MediaEncodingProfile.CreateM4a(quality),
            "wav" => MediaEncodingProfile.CreateWav(quality),
            "flac" => MediaEncodingProfile.CreateFlac(quality),
            "wma" => MediaEncodingProfile.CreateWma(quality),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        profile.Video = null;
        await TranscodeAsync(source, destination, _ => Task.FromResult(profile), progress, token);
    }

    public static Task ToVideoAsync(string source, string destination, string container, VideoOptions options, IProgress<double> progress, CancellationToken token) =>
        TranscodeAsync(source, destination, async input =>
        {
            var original = await MediaEncodingProfile.CreateFromFileAsync(input);
            var video = original.Video ?? throw new InvalidOperationException("В файле нет видеодорожки.");
            uint width = video.Width, height = video.Height;
            if (options.MaxHeight > 0 && height > options.MaxHeight)
            {
                width = (uint)Math.Round(width * (double)options.MaxHeight / height);
                height = options.MaxHeight;
            }
            if (container != "wmv")
            {
                // Кодировщик H.264 не принимает кадр больше 4K — вписываем в 3840×2160 (или 2160×3840 для вертикального).
                var (maxW, maxH) = width >= height ? (3840.0, 2160.0) : (2160.0, 3840.0);
                var fit = Math.Min(1, Math.Min(maxW / width, maxH / height));
                width = (uint)Math.Round(width * fit);
                height = (uint)Math.Round(height * fit);
            }
            // H.264 требует чётных размеров.
            width &= ~1u;
            height &= ~1u;

            var profile = container == "wmv"
                ? MediaEncodingProfile.CreateWmv(VideoEncodingQuality.HD1080p)
                : MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = width;
            profile.Video.Height = height;
            profile.Video.PixelAspectRatio.Numerator = 1;
            profile.Video.PixelAspectRatio.Denominator = 1;
            double fps = 30;
            if (video.FrameRate.Numerator > 0 && video.FrameRate.Denominator > 0)
            {
                profile.Video.FrameRate.Numerator = video.FrameRate.Numerator;
                profile.Video.FrameRate.Denominator = video.FrameRate.Denominator;
                fps = (double)video.FrameRate.Numerator / video.FrameRate.Denominator;
            }
            var bitrate = width * height * Math.Clamp(fps, 10, 60) * options.BitsPerPixel;
            if (video.Bitrate > 0 && options.MaxHeight == 0 && options.BitsPerPixel < 0.06)
                bitrate = Math.Min(bitrate, video.Bitrate * 0.6); // «Сжать» всегда даёт файл меньше исходного
            profile.Video.Bitrate = (uint)Math.Clamp(bitrate, 300_000, 40_000_000);

            if (options.Mute || original.Audio is null)
            {
                profile.Audio = null;
            }
            else
            {
                profile.Audio.Bitrate = options.BitsPerPixel < 0.06 ? 128_000u : 192_000u;
                profile.Audio.ChannelCount = Math.Clamp(original.Audio.ChannelCount, 1u, 2u);
                profile.Audio.SampleRate = original.Audio.SampleRate is 44100 or 48000 ? original.Audio.SampleRate : 48000;
            }
            return profile;
        }, progress, token);

    /// <summary>
    /// Правки из видеоредактора. Crop — область кадра в пикселях исходника («приблизить»): она растягивается до размера
    /// исходного кадра, если у неё те же пропорции, иначе вписывается в него.
    /// Поворота и отражения нет: эффект Windows поворачивает с полями и смещением, а отражение пропускает.
    /// </summary>
    public sealed record VideoEdit(TimeSpan Start, TimeSpan? End, Windows.Foundation.Rect? Crop, bool Mute, uint MaxHeight);

    public static async Task<(uint Width, uint Height, TimeSpan Duration)> ProbeVideoAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var profile = await MediaEncodingProfile.CreateFromFileAsync(file);
        var video = profile.Video ?? throw new InvalidOperationException("В файле нет видеодорожки.");
        var duration = (await file.Properties.GetVideoPropertiesAsync()).Duration;
        // У фрагментированных MP4 в заголовке длительность нулевая — складываем её из фрагментов
        if (duration <= TimeSpan.Zero)
            duration = Mp4Duration.TryRead(path) ?? TimeSpan.Zero;
        return (video.Width, video.Height, duration);
    }

    /// <summary>Для видеоредактора: размеры, длина, есть ли звук и частота кадров.</summary>
    public sealed record ClipInfo(uint Width, uint Height, TimeSpan Duration, bool HasAudio, uint FrameRateNumerator, uint FrameRateDenominator);

    public static async Task<ClipInfo> ProbeClipAsync(string path)
    {
        var (width, height, duration) = await ProbeVideoAsync(path);
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var profile = await Windows.Media.MediaProperties.MediaEncodingProfile.CreateFromFileAsync(file);
        var rate = profile.Video?.FrameRate;
        var (numerator, denominator) = rate is { Numerator: > 0, Denominator: > 0 } ? (rate.Numerator, rate.Denominator) : (30u, 1u);
        return new ClipInfo(width, height, duration, profile.Audio is not null, numerator, denominator);
    }

    /// <summary>Кадр из видео в PNG — в полном размере, но не больше 4K. Crop — часть кадра в пикселях исходника.</summary>
    public static async Task SaveFrameAsync(string source, TimeSpan time, string destination, Windows.Foundation.Rect? crop = null, CancellationToken token = default)
    {
        var (width, height, duration) = await ProbeVideoAsync(source);
        var fit = Math.Min(1, 3840.0 / Math.Max(width, height));
        string? temp = null;
        try
        {
            Windows.Media.Editing.MediaClip clip;
            try
            {
                clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(source));
            }
            catch (ArgumentException)
            {
                // Монтаж Windows открывает не всякий MP4 (например, без длительности в заголовке) —
                // перекодируем полсекунды с нужного места: перекодировщик такие файлы читает
                temp = Path.Combine(Path.GetTempPath(), $"orbit-frame-{Guid.NewGuid():N}.mp4");
                var end = time + TimeSpan.FromSeconds(0.5);
                await EditVideoAsync(source, temp, new VideoEdit(time, end < duration ? end : null, null, true, 0), 0.2, new Progress<double>(), token);
                clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(temp));
                time = TimeSpan.Zero;
            }
            var composition = new Windows.Media.Editing.MediaComposition();
            composition.Clips.Add(clip);
            using var frame = await composition.GetThumbnailAsync(time, (int)(width * fit), (int)(height * fit),
                Windows.Media.Editing.VideoFramePrecision.NearestFrame).AsTask(token);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(frame);
            var transform = new Windows.Graphics.Imaging.BitmapTransform();
            if (crop is { } area)
            {
                var x = (uint)Math.Clamp(area.X * fit, 0, decoder.PixelWidth - 1);
                var y = (uint)Math.Clamp(area.Y * fit, 0, decoder.PixelHeight - 1);
                transform.Bounds = new Windows.Graphics.Imaging.BitmapBounds
                {
                    X = x,
                    Y = y,
                    Width = (uint)Math.Clamp(area.Width * fit, 1, decoder.PixelWidth - x),
                    Height = (uint)Math.Clamp(area.Height * fit, 1, decoder.PixelHeight - y),
                };
            }
            using var bitmap = await decoder.GetSoftwareBitmapAsync(decoder.BitmapPixelFormat, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, transform,
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destination)!);
            var file = await folder.CreateFileAsync(Path.GetFileName(destination), CreationCollisionOption.ReplaceExisting);
            using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
        }
        finally
        {
            if (temp is not null)
                TryDelete(temp);
        }
    }

    /// <summary>Размер результата правок: область кадра растягивается до исходного размера, затем MaxHeight и предел 4K; стороны чётные.</summary>
    private static (Windows.Foundation.Rect Crop, uint Width, uint Height) EditGeometry(uint sourceWidth, uint sourceHeight, VideoEdit edit)
    {
        var crop = edit.Crop ?? new Windows.Foundation.Rect(0, 0, sourceWidth, sourceHeight);
        // Кодировщику нужны чётные размеры и области
        crop = new Windows.Foundation.Rect(Math.Floor(crop.X / 2) * 2, Math.Floor(crop.Y / 2) * 2,
            Math.Max(16, Math.Floor(crop.Width / 2) * 2), Math.Max(16, Math.Floor(crop.Height / 2) * 2));
        var fit = Math.Min(sourceWidth / crop.Width, sourceHeight / crop.Height);
        double width = crop.Width * fit, height = crop.Height * fit;
        if (edit.MaxHeight > 0 && height > edit.MaxHeight)
            (width, height) = (width * edit.MaxHeight / height, edit.MaxHeight);
        var (maxW, maxH) = width >= height ? (3840.0, 2160.0) : (2160.0, 3840.0);
        var limit = Math.Min(1, Math.Min(maxW / width, maxH / height));
        return (crop, (uint)Math.Round(width * limit) & ~1u, (uint)Math.Round(height * limit) & ~1u);
    }

    private static MediaEncodingProfile EditProfile(MediaEncodingProfile original, uint width, uint height, bool mute, double bitsPerPixel)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = width;
        profile.Video.Height = height;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        double fps = 30;
        if (original.Video is { FrameRate: { Numerator: > 0, Denominator: > 0 } rate })
        {
            profile.Video.FrameRate.Numerator = rate.Numerator;
            profile.Video.FrameRate.Denominator = rate.Denominator;
            fps = (double)rate.Numerator / rate.Denominator;
        }
        profile.Video.Bitrate = (uint)Math.Clamp(width * height * Math.Clamp(fps, 10, 60) * bitsPerPixel, 300_000, 40_000_000);
        if (mute)
            profile.Audio = null;
        else
        {
            profile.Audio.Bitrate = 192_000;
            profile.Audio.ChannelCount = original.Audio is { } audio ? Math.Clamp(audio.ChannelCount, 1u, 2u) : 2;
            profile.Audio.SampleRate = original.Audio?.SampleRate is 44100 or 48000 ? original.Audio.SampleRate : 48000;
        }
        return profile;
    }

    public static async Task EditVideoAsync(string source, string destination, VideoEdit edit, double bitsPerPixel, IProgress<double> progress, CancellationToken token)
    {
        var (sourceWidth, sourceHeight, duration) = await ProbeVideoAsync(source);
        var (crop, frameWidth, frameHeight) = EditGeometry(sourceWidth, sourceHeight, edit);

        await TranscodeAsync(source, destination, async input =>
        {
            var original = await MediaEncodingProfile.CreateFromFileAsync(input);
            return EditProfile(original, frameWidth, frameHeight, edit.Mute || original.Audio is null, bitsPerPixel);
        }, progress, token, transcoder =>
        {
            transcoder.TrimStartTime = edit.Start;
            // Вопреки документации это момент конца, а не сколько отрезать с конца (проверено на Windows 11 26200)
            if (edit.End is { } end && end < duration)
                transcoder.TrimStopTime = end;
            var transform = new Windows.Media.Effects.VideoTransformEffectDefinition
            {
                CropRectangle = crop,
                OutputSize = new Windows.Foundation.Size(frameWidth, frameHeight),
                ProcessingAlgorithm = Windows.Media.Transcoding.MediaVideoProcessingAlgorithm.Default,
            };
            transcoder.AddVideoEffect(transform.ActivatableClassId, true, transform.Properties);
        });
    }

    /// <summary>
    /// Склейка роликов по порядку в один MP4. Ролик под номером edited получает правки редактора (edit), остальные идут
    /// целиком; размер результата — как у ролика с правками, другие пропорции вписываются с полями. Ролик с правками
    /// сначала перекодируется отдельно, остальные идут в монтаж Windows как есть, а если он их не открывает — тоже через перекодирование.
    /// </summary>
    public static async Task JoinVideosAsync(IReadOnlyList<string> sources, int edited, string destination, VideoEdit edit, double bitsPerPixel,
        IProgress<double> progress, CancellationToken token)
    {
        var (sourceWidth, sourceHeight, _) = await ProbeVideoAsync(sources[edited]);
        var (_, frameWidth, frameHeight) = EditGeometry(sourceWidth, sourceHeight, edit);
        var original = await MediaEncodingProfile.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(sources[edited]));
        var temps = new List<string>();
        // Каждый ролик — шаг прогресса, сборка — ещё один
        var steps = sources.Count + 1;
        var done = 0;
        try
        {
            var composition = new Windows.Media.Editing.MediaComposition();
            for (var i = 0; i < sources.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                Windows.Media.Editing.MediaClip? clip = null;
                if (i != edited)
                {
                    try
                    {
                        clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(sources[i]));
                    }
                    catch (ArgumentException)
                    {
                        // откроем после перекодирования
                    }
                }
                if (clip is null)
                {
                    var temp = Path.Combine(Path.GetTempPath(), $"orbit-join-{Guid.NewGuid():N}.mp4");
                    temps.Add(temp);
                    var part = i == edited ? edit : new VideoEdit(TimeSpan.Zero, null, null, edit.Mute, 0);
                    var step = done;
                    await EditVideoAsync(sources[i], temp, part, Math.Max(bitsPerPixel, 0.15),
                        new Progress<double>(value => progress.Report((step + value) / steps)), token);
                    clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(temp));
                }
                if (edit.Mute)
                    clip.Volume = 0;
                composition.Clips.Add(clip);
                progress.Report((double)++done / steps);
            }

            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destination)!);
            var output = await folder.CreateFileAsync(Path.GetFileName(destination), CreationCollisionOption.ReplaceExisting);
            var profile = EditProfile(original, frameWidth, frameHeight, edit.Mute, bitsPerPixel);
            var result = await composition.RenderToFileAsync(output, Windows.Media.Editing.MediaTrimmingPreference.Precise, profile)
                .AsTask(token, new Progress<double>(percent => progress.Report((done + Math.Clamp(percent / 100.0, 0, 1)) / steps)));
            if (result != TranscodeFailureReason.None)
                throw new InvalidOperationException($"Windows не смогла склеить видео ({result}).");
        }
        finally
        {
            foreach (var temp in temps)
                TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // временный файл удалит система
        }
    }

    private static async Task TranscodeAsync(string source, string destination, Func<StorageFile, Task<MediaEncodingProfile>> buildProfile, IProgress<double> progress, CancellationToken token,
        Action<MediaTranscoder>? configure = null)
    {
        var input = await StorageFile.GetFileFromPathAsync(source);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destination)!);
        var output = await folder.CreateFileAsync(Path.GetFileName(destination), CreationCollisionOption.ReplaceExisting);
        var profile = await buildProfile(input);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        configure?.Invoke(transcoder);
        var prepared = await transcoder.PrepareFileTranscodeAsync(input, output, profile);
        if (!prepared.CanTranscode)
        {
            throw new InvalidOperationException(prepared.FailureReason switch
            {
                TranscodeFailureReason.CodecNotFound => "В Windows нет кодека для этого файла. Установите «Расширения для видео HEVC/VP9/веб-медиа» из Microsoft Store.",
                TranscodeFailureReason.InvalidProfile => "Этот формат нельзя получить из исходного файла.",
                _ => $"Windows не смогла перекодировать файл ({prepared.FailureReason}).",
            });
        }
        var reporter = new Progress<double>(percent => progress.Report(Math.Clamp(percent / 100.0, 0, 1)));
        await prepared.TranscodeAsync().AsTask(token, reporter);
    }
}
