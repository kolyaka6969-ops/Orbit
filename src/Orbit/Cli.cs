using Orbit.Core;

namespace Orbit;

/// <summary>
/// Режим без интерфейса для проверки и автоматизации:
///   Orbit.exe --list файлы…          — какие пункты есть на каждом колесе;
///   Orbit.exe --run id файлы…        — выполнить пункт и вывести пути результатов.
/// Код выхода 0 — успех, 1 — ошибка.
/// </summary>
internal static class Cli
{
    public static void Run(App app, string[] args)
    {
        app.Dispatcher.BeginInvoke(async () =>
        {
            var code = 0;
            using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            try
            {
                var operations = Catalog.Build();
                if (args[0] == "--render")
                {
                    await UI.Preview.RenderAllAsync(app, Path.GetFullPath(args[1]), args.Skip(2).ToList());
                    output.WriteLine("rendered");
                }
                else if (args[0] == "--probe")
                {
                    // Что видит Media Foundation в файле — для диагностики видео и аудио.
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(args[1]));
                    var profile = await Windows.Media.MediaProperties.MediaEncodingProfile.CreateFromFileAsync(file);
                    if (profile.Video is { } v)
                        output.WriteLine($"video: {v.Subtype} {v.Width}x{v.Height} {v.FrameRate.Numerator}/{v.FrameRate.Denominator} fps {v.Bitrate} bps");
                    if (profile.Audio is { } a)
                        output.WriteLine($"audio: {a.Subtype} {a.SampleRate} Hz {a.ChannelCount} ch {a.Bitrate} bps");
                }
                else if (args[0] == "--list")
                {
                    var items = args.Skip(1).Select(p => new FileItem(p)).ToList();
                    foreach (var wheel in Enum.GetValues<WheelKind>())
                        output.WriteLine($"{wheel}: {string.Join(", ", operations.Where(o => o.Wheel == wheel && o.AppliesTo(items)).Select(o => o.Id))}");
                }
                else
                {
                    var op = operations.FirstOrDefault(o => o.Id == args[1]) ?? throw new ArgumentException($"Нет операции «{args[1]}».");
                    var items = args.Skip(2).Select(p => new FileItem(p)).ToList();
                    if (!op.AppliesTo(items))
                        throw new ArgumentException($"«{op.Id}» не подходит для этих файлов.");
                    var context = new JobContext { Settings = new Settings() };
                    var results = await op.Run(items, context);
                    foreach (var result in results)
                        output.WriteLine(result);
                    if (context.Message is not null)
                        output.WriteLine($"# {context.Message}");
                }
            }
            catch (Exception ex)
            {
                output.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
                code = 1;
            }
            app.Shutdown(code);
        });
    }
}
