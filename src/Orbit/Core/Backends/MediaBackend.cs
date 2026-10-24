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

    private static async Task TranscodeAsync(string source, string destination, Func<StorageFile, Task<MediaEncodingProfile>> buildProfile, IProgress<double> progress, CancellationToken token)
    {
        var input = await StorageFile.GetFileFromPathAsync(source);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destination)!);
        var output = await folder.CreateFileAsync(Path.GetFileName(destination), CreationCollisionOption.ReplaceExisting);
        var profile = await buildProfile(input);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
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
