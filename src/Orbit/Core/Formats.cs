namespace Orbit.Core;

public enum FileKind
{
    Other,
    Folder,
    Image,
    Raw,
    Audio,
    Video,
    Pdf,
    Archive,
    Document,
    Spreadsheet,
}

public static class Formats
{
    public static readonly HashSet<string> Images = Set("png jpg jpeg jfif jpe webp avif heic heif bmp gif tif tiff ico tga psd jxl qoi");

    /// <summary>RAW с фотокамер (читает libraw внутри Magick.NET).</summary>
    public static readonly HashSet<string> Raw = Set("cr2 cr3 crw nef nrw arw srf sr2 dng raf orf rw2 pef srw x3f 3fr erf kdc mrw iiq");

    /// <summary>То, что декодирует Media Foundation в Windows 10/11.</summary>
    public static readonly HashSet<string> Audio = Set("mp3 m4a aac wav flac wma ogg oga opus amr 3ga");

    public static readonly HashSet<string> Video = Set("mp4 m4v mov mkv avi wmv webm 3gp 3g2 ts mts m2ts");

    /// <summary>Документы Word и совместимые с ним.</summary>
    public static readonly HashSet<string> Documents = Set("docx doc docm dotx dot dotm rtf odt");

    /// <summary>Таблицы Excel и совместимые с ним.</summary>
    public static readonly HashSet<string> Spreadsheets = Set("xlsx xls xlsm xlsb xltx xlt xltm ods csv");

    public static readonly HashSet<string> Archives = Set("zip 7z rar tar gz tgz bz2 tbz2 xz txz");

    public static FileKind KindOf(string extension) =>
        Images.Contains(extension) ? FileKind.Image
        : Raw.Contains(extension) ? FileKind.Raw
        : Audio.Contains(extension) ? FileKind.Audio
        : Video.Contains(extension) ? FileKind.Video
        : extension == "pdf" ? FileKind.Pdf
        : Archives.Contains(extension) ? FileKind.Archive
        : Documents.Contains(extension) ? FileKind.Document
        : Spreadsheets.Contains(extension) ? FileKind.Spreadsheet
        : FileKind.Other;

    /// <summary>Одно имя для синонимов: jpeg → jpg, tif → tiff.</summary>
    public static string Canonical(string extension) => extension switch
    {
        "jpeg" or "jfif" or "jpe" => "jpg",
        "tif" => "tiff",
        "heif" => "heic",
        "oga" => "ogg",
        _ => extension,
    };

    public static bool IsPicture(FileItem item) => item.Kind is FileKind.Image or FileKind.Raw;

    private static HashSet<string> Set(string list) =>
        new(list.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
}
