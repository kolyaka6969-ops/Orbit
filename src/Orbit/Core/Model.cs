namespace Orbit.Core;

/// <summary>Три круговых меню: Shift — конвертация, Ctrl — инструменты, Alt — действия с файлами.</summary>
public enum WheelKind
{
    Convert,
    Tools,
    Actions,
}

public sealed class FileItem
{
    public FileItem(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        IsDirectory = System.IO.Directory.Exists(Path);
        Extension = IsDirectory ? "" : System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant();
        Kind = IsDirectory ? FileKind.Folder : Formats.KindOf(Extension);
    }

    public string Path { get; }

    public bool IsDirectory { get; }

    /// <summary>Расширение без точки, в нижнем регистре.</summary>
    public string Extension { get; }

    public FileKind Kind { get; }

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));

    /// <summary>Имя без расширения; для «.tar.gz» отрезаются оба.</summary>
    public string BaseName
    {
        get
        {
            if (IsDirectory)
                return Name;
            var name = System.IO.Path.GetFileNameWithoutExtension(Path);
            return name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }
    }

    public string Folder => System.IO.Path.GetDirectoryName(Path.TrimEnd('\\')) ?? Path;

    public override string ToString() => Path;
}

/// <summary>Всё, что нужно операции во время работы.</summary>
public sealed class JobContext
{
    public required Settings Settings { get; init; }

    public CancellationToken Token { get; init; }

    public IProgress<double> Progress { get; init; } = new Progress<double>();

    /// <summary>Текст вместо стандартного «Готово» (например, «Путь скопирован»).</summary>
    public string? Message { get; set; }

    public string OutputDirFor(FileItem item) => Settings.Output switch
    {
        OutputMode.Subfolder => System.IO.Path.Combine(item.Folder, "Orbit"),
        OutputMode.Custom when !string.IsNullOrWhiteSpace(Settings.CustomFolder) => Settings.CustomFolder!,
        _ => item.Folder,
    };
}

/// <summary>Пункт кругового меню.</summary>
public sealed class Operation
{
    public required string Id { get; init; }

    public required WheelKind Wheel { get; init; }

    public required string Label { get; init; }

    /// <summary>Вторая строка под названием, например «только звук».</summary>
    public string? Subtitle { get; init; }

    /// <summary>Символ из шрифта Segoe Fluent Icons.</summary>
    public required string Glyph { get; init; }

    public required Func<IReadOnlyList<FileItem>, bool> AppliesTo { get; init; }

    /// <summary>Выполняет операцию и возвращает созданные или изменённые файлы.</summary>
    public required Func<IReadOnlyList<FileItem>, JobContext, Task<IReadOnlyList<string>>> Run { get; init; }

    /// <summary>Мгновенное действие (копирование пути и т. п.): без полосы прогресса.</summary>
    public bool Instant { get; init; }

    /// <summary>Открывает своё окно (редактор): панель с прогрессом не нужна, а закрытие без сохранения — не новость.</summary>
    public bool Interactive { get; init; }

    public override string ToString() => Id;
}
