namespace Orbit.Core;

/// <summary>
/// Имена результатов. Исходные файлы никогда не перезаписываются: если имя занято, добавляется « (2)», « (3)»…
/// Результат сначала пишется во временный файл, поэтому прерванная конвертация не оставляет половину файла.
/// </summary>
public static class OutputPaths
{
    private static readonly object Gate = new();

    public static string Unique(string dir, string baseName, string extension)
    {
        var ext = string.IsNullOrEmpty(extension) ? "" : "." + extension;
        var candidate = Path.Combine(dir, baseName + ext);
        for (var i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{baseName} ({i}){ext}");
        return candidate;
    }

    public static string UniqueFolder(string dir, string name) => Unique(dir, name, "");

    public static async Task<string> ProduceAsync(string dir, string baseName, string extension, Func<string, Task> write)
    {
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".orbit-{Guid.NewGuid():N}.{extension}");
        try
        {
            await write(temp);
            if (!File.Exists(temp))
                throw new IOException("Результат не был создан.");
            lock (Gate)
            {
                var final = Unique(dir, baseName, extension);
                File.Move(temp, final);
                return final;
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
