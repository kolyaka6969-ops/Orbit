using Orbit.Core;

namespace Orbit;

/// <summary>
/// Режим без интерфейса для проверки и автоматизации:
///   Orbit.exe --list файлы…          — какие пункты есть на каждом колесе;
///   Orbit.exe --run id файлы…        — выполнить пункт и вывести пути результатов
///                                      (--office Auto|MicrosoftOffice|ArtCraft|LibreOffice — чем открывать Word и Excel).
///   Orbit.exe --video-edit …          — правки видеоредактора без окна (параметры — у разбора ниже);
///   Orbit.exe --frame видео секунда png — кадр из видео;
///   Orbit.exe --join результат видео…   — склейка роликов.
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
                    output.WriteLine($"duration: {(await Core.Backends.MediaBackend.ProbeVideoAsync(file.Path)).Duration}");
                }
                else if (args[0] == "--frame")
                {
                    // --frame видео секунда кадр.png — кадр из видео, для проверки правок
                    await Core.Backends.MediaBackend.SaveFrameAsync(Path.GetFullPath(args[1]),
                        TimeSpan.FromSeconds(double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)), Path.GetFullPath(args[3]));
                    output.WriteLine(args[3]);
                }
                else if (args[0] == "--video-edit")
                {
                    // --video-edit исходник результат начало_с конец_с|- x,y,w,h|- без_звука(0|1) высота|0
                    var crop = args[5] == "-" ? (Windows.Foundation.Rect?)null
                        : args[5].Split(',').Select(double.Parse).ToArray() is var c ? new Windows.Foundation.Rect(c[0], c[1], c[2], c[3]) : null;
                    var edit = new Core.Backends.MediaBackend.VideoEdit(TimeSpan.FromSeconds(double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)),
                        args[4] == "-" ? null : TimeSpan.FromSeconds(double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture)),
                        crop, args[6] == "1", uint.Parse(args[7]));
                    await Core.Backends.MediaBackend.EditVideoAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), edit, 0.08, new Progress<double>(), CancellationToken.None);
                    output.WriteLine(args[2]);
                }
                else if (args[0] == "--join")
                {
                    // --join результат первый второй… — склейка без правок
                    var sources = args.Skip(2).Select(Path.GetFullPath).ToList();
                    await Core.Backends.MediaBackend.JoinVideosAsync(sources, 0, Path.GetFullPath(args[1]),
                        new Core.Backends.MediaBackend.VideoEdit(TimeSpan.Zero, null, null, false, 0), 0.08, new Progress<double>(), CancellationToken.None);
                    output.WriteLine(args[1]);
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
                    var settings = new Settings();
                    var rest = args.Skip(2).ToList();
                    if (rest.IndexOf("--office") is var at and >= 0 && at + 1 < rest.Count)
                    {
                        settings.Office = Enum.Parse<OfficeEngine>(rest[at + 1], ignoreCase: true);
                        rest.RemoveRange(at, 2);
                    }
                    var items = rest.Select(p => new FileItem(p)).ToList();
                    if (!op.AppliesTo(items))
                        throw new ArgumentException($"«{op.Id}» не подходит для этих файлов.");
                    var context = new JobContext { Settings = settings };
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
