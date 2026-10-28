using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit.Core;

public enum OutputMode
{
    /// <summary>Рядом с исходным файлом.</summary>
    SameFolder,

    /// <summary>В подпапку «Orbit» рядом с исходным файлом.</summary>
    Subfolder,

    /// <summary>В выбранную пользователем папку.</summary>
    Custom,
}

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

public enum VideoQuality
{
    Small,
    Balanced,
    High,
}

/// <summary>Чем конвертировать документы Word и таблицы Excel.</summary>
public enum OfficeEngine
{
    /// <summary>Первое, что установлено: Microsoft Office, ArtCraft, LibreOffice.</summary>
    Auto,
    MicrosoftOffice,

    /// <summary>WordCraft и GridCraft из ArtCraft.</summary>
    ArtCraft,
    LibreOffice,
}

/// <summary>
/// Настройки в %APPDATA%\Orbit\settings.json. Автозапуск и пункт контекстного меню хранятся в реестре —
/// там их видит и Windows, и установщик.
/// </summary>
public sealed class Settings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbit");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    public bool ShiftWheel { get; set; } = true;

    public bool CtrlWheel { get; set; } = true;

    public bool AltWheel { get; set; } = true;

    public bool HotkeyEnabled { get; set; } = true;

    public bool Paused { get; set; }

    public bool ShowNotifications { get; set; } = true;

    public OutputMode Output { get; set; } = OutputMode.SameFolder;

    public string? CustomFolder { get; set; }

    public int ImageQuality { get; set; } = 85;

    public VideoQuality Video { get; set; } = VideoQuality.Balanced;

    /// <summary>Куда видеоредактор сохранял в прошлый раз — с этой папки откроется «Сохранить».</summary>
    public string? VideoSaveFolder { get; set; }

    public int PdfDpi { get; set; } = 150;

    public OfficeEngine Office { get; set; } = OfficeEngine.Auto;

    public bool Welcomed { get; set; }

    public event Action? Changed;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Повреждённый файл — начинаем с настроек по умолчанию.
        }
        return new Settings();
    }

    public void Save()
    {
        ImageQuality = Math.Clamp(ImageQuality, 10, 100);
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        Changed?.Invoke();
    }

    public WheelKind? WheelFor(ModifierKey key) => key switch
    {
        ModifierKey.Shift when ShiftWheel => WheelKind.Convert,
        ModifierKey.Ctrl when CtrlWheel => WheelKind.Tools,
        ModifierKey.Alt when AltWheel => WheelKind.Actions,
        _ => null,
    };
}

public enum ModifierKey
{
    None,
    Shift,
    Ctrl,
    Alt,
}
