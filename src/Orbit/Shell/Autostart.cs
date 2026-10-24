using Microsoft.Win32;

namespace Orbit.Shell;

/// <summary>
/// Автозапуск через HKCU\…\Run — так же его включает установщик. Учитывается и выключение в «Диспетчере задач»
/// (StartupApproved): если пользователь отключил Orbit там, переключатель в настройках покажет «Откл.».
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "Orbit";

    public const string Argument = "--autostart";

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(Name) is not string)
                return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // Нечётный первый байт — запуск отключён пользователем в «Диспетчере задач» или «Параметрах».
            return approved?.GetValue(Name) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
    }

    public static void Set(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            run.SetValue(Name, Command);
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(Name, throwOnMissingValue: false);
        }
        else
        {
            run.DeleteValue(Name, throwOnMissingValue: false);
        }
    }

    /// <summary>Если программу переустановили в другую папку, обновляет путь в автозапуске.</summary>
    public static void RefreshPath()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (run?.GetValue(Name) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            run.SetValue(Name, Command);
    }
}
