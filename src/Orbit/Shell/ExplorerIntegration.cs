using Microsoft.Win32;
using Orbit.Core;

namespace Orbit.Shell;

/// <summary>
/// Пункт «Открыть в Orbit» в контекстном меню Проводника (в Windows 11 — в «Показать дополнительные параметры»)
/// и чтение выделенных в Проводнике файлов для горячей клавиши.
/// </summary>
internal static class ExplorerIntegration
{
    private static readonly string[] Roots = [@"Software\Classes\*\shell\Orbit", @"Software\Classes\Directory\shell\Orbit"];

    public static bool IsContextMenuEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Roots[0] + @"\command");
            return key?.GetValue(null) is string;
        }
    }

    public static void SetContextMenu(bool enabled)
    {
        var exe = Environment.ProcessPath!;
        foreach (var root in Roots)
        {
            if (!enabled)
            {
                Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
                continue;
            }
            using var key = Registry.CurrentUser.CreateSubKey(root);
            key.SetValue(null, "Открыть в Orbit");
            key.SetValue("Icon", $"\"{exe}\",0");
            // Пункт виден при любом числе выделенных файлов; каждый файл приходит отдельным запуском.
            key.SetValue("MultiSelectModel", "Player");
            using var command = key.CreateSubKey("command");
            command.SetValue(null, $"\"{exe}\" \"%1\"");
        }
    }

    public static void RefreshPath()
    {
        if (IsContextMenuEnabled)
            SetContextMenu(true);
    }

    /// <summary>Выделенные файлы в активном окне Проводника; если там пусто — файлы из буфера обмена.</summary>
    public static IReadOnlyList<string> SelectedFiles(IntPtr foreground)
    {
        var fromExplorer = FromExplorer(foreground);
        return fromExplorer.Count > 0 ? fromExplorer : FromClipboard();
    }

    public static IReadOnlyList<string> FromClipboard()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsFileDropList())
                return System.Windows.Clipboard.GetFileDropList().Cast<string>().Where(Exists).ToList();
            if (System.Windows.Clipboard.ContainsImage() && System.Windows.Clipboard.GetImage() is { } image)
            {
                // Снимок экрана (Win+Shift+S) сохраняем в «Загрузки\Orbit», чтобы с ним можно было работать как с файлом.
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Orbit");
                Directory.CreateDirectory(dir);
                var target = OutputPaths.Unique(dir, $"Снимок экрана {DateTime.Now:yyyy-MM-dd HHmmss}", "png");
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using var stream = File.Create(target);
                encoder.Save(stream);
                return [target];
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException or IOException)
        {
            // Буфер занят или не читается.
        }
        return [];
    }

    private static List<string> FromExplorer(IntPtr foreground)
    {
        var best = new List<string>();
        var title = Native.WindowText(foreground);
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
                return best;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic windows = shell.Windows();
            int count = windows.Count;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    dynamic? window = windows.Item(i);
                    if (window is null || new IntPtr((long)window.HWND) != foreground)
                        continue;
                    var paths = new List<string>();
                    dynamic selected = window.Document.SelectedItems();
                    int selectedCount = selected.Count;
                    for (var j = 0; j < selectedCount; j++)
                        paths.Add((string)selected.Item(j).Path);
                    if (paths.Count == 0)
                        continue;
                    // В Windows 11 вкладки Проводника делят одно окно; активная вкладка — та, чьё имя в заголовке.
                    string location = window.LocationName;
                    if (best.Count == 0 || title.StartsWith(location, StringComparison.OrdinalIgnoreCase))
                        best = paths;
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
                {
                    // Не файловое окно (например, Панель управления).
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
        }
        return best.Where(Exists).ToList();
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
