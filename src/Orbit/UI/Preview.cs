using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orbit.Core;

namespace Orbit.UI;

/// <summary>
/// Режим «--render папка»: рисует окна Orbit в PNG, не показывая их на экране (для документации и проверки вёрстки).
/// Фон Mica вне экрана недоступен, поэтому подкладывается сплошной цвет темы.
/// </summary>
internal static class Preview
{
    public static async Task RenderAllAsync(App app, string folder, IReadOnlyList<string> samples)
    {
        Directory.CreateDirectory(folder);
        var items = samples.Select(p => new FileItem(p)).ToList();
        foreach (var dark in new[] { false, true })
        {
            ThemeManager.Apply(dark ? ThemeChoice.Dark : ThemeChoice.Light);
            var suffix = dark ? "dark" : "light";

            var settings = new SettingsWindow(app);
            var pages = new (string Name, Action Select)[]
            {
                ("general", () => settings.NavGeneral.IsChecked = true),
                ("gestures", () => settings.NavGestures.IsChecked = true),
                ("output", () => settings.NavOutput.IsChecked = true),
                ("formats", () => settings.NavFormats.IsChecked = true),
                ("about", () => settings.NavAbout.IsChecked = true),
            };
            var content = Detach(settings);
            foreach (var (name, select) in pages)
            {
                select();
                await SaveAsync(content, 1040, 720, Path.Combine(folder, $"settings-{name}-{suffix}.png"));
                if (!dark && name != "general")
                    continue;
            }

            var operations = Catalog.Build();
            foreach (var (wheel, highlight) in new[] { (WheelKind.Convert, 1), (WheelKind.Tools, 0), (WheelKind.Actions, 2) })
            {
                var window = new WheelWindow(WheelMode.Click, wheel, items,
                    (w, targets) => operations.Where(o => o.Wheel == w && o.AppliesTo(targets)).ToList(), (_, _) => { });
                window.PrepareForPreview(highlight);
                var canvas = Detach(window);
                var side = ((FrameworkElement)((Border)canvas).Child).Width;
                await SaveAsync(canvas, side, side, Path.Combine(folder, $"wheel-{wheel.ToString().ToLowerInvariant()}-{suffix}.png"), transparent: true);
            }

            var runner = new JobRunner(app.Settings, app.Dispatcher);
            var flyout = new JobsFlyout(runner);
            var done = new Job(operations.First(o => o.Id == "image.to.webp"), items);
            done.Complete([Path.ChangeExtension(items[0].Path, "webp")], null);
            var running = new Job(operations.First(o => o.Id == "video.compress"), [new FileItem(Path.Combine(Path.GetTempPath(), "Отпуск 2026.mp4"))]);
            running.Progress = 0.42;
            runner.Jobs.Add(running);
            runner.Jobs.Add(done);
            runner.Jobs.Add(Job.Info("Ничего не выбрано", "Выделите файлы в Проводнике и нажмите Ctrl + Alt + Пробел ещё раз."));
            await SaveAsync(Detach(flyout), 392, double.NaN, Path.Combine(folder, $"jobs-{suffix}.png"), transparent: true);

            await SaveAsync(Detach(new TrayMenu(app)), 292, double.NaN, Path.Combine(folder, $"tray-{suffix}.png"), transparent: true);
        }
    }

    private static FrameworkElement Detach(Window window)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new Border { Child = content };
        if (window.Background is SolidColorBrush { Color.A: > 0 } || window is SettingsWindow)
            host.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
        host.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        host.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
        TextElement.SetFontSize(host, 14);
        return host;
    }

    private static async Task SaveAsync(FrameworkElement element, double width, double height, string path, bool transparent = false)
    {
        var size = new Size(width, double.IsNaN(height) ? double.PositiveInfinity : height);
        element.Measure(size);
        var final = new Size(width, double.IsNaN(height) ? element.DesiredSize.Height : height);
        element.Arrange(new Rect(final));
        element.UpdateLayout();
        // Даём отработать анимациям переключателей и появлению секторов.
        await Task.Delay(700);
        element.UpdateLayout();
        const double scale = 1.5;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(final.Width * scale), (int)Math.Ceiling(final.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
