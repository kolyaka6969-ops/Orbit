using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Orbit.Core.Backends;

/// <summary>
/// Документы Word и таблицы Excel. Конвертирует первая подходящая из установленных программ: Microsoft Office
/// (через COM, без окон и макросов), WordCraft и GridCraft из ArtCraft (через их командную строку) или LibreOffice
/// в фоновом режиме. Без них эти пункты сообщают, что нужно установить.
/// </summary>
public static class OfficeBackend
{
    /// <summary>Документ → (расширение, подпись).</summary>
    public static readonly (string Ext, string? Subtitle)[] DocumentTargets =
    [
        ("pdf", null),
        ("docx", "Word"),
        ("doc", "Word 97–2003"),
        ("rtf", null),
        ("odt", "OpenDocument"),
        ("txt", "только текст"),
    ];

    /// <summary>Таблица → (расширение, подпись).</summary>
    public static readonly (string Ext, string? Subtitle)[] SpreadsheetTargets =
    [
        ("pdf", "все листы"),
        ("xlsx", "Excel"),
        ("xls", "Excel 97–2003"),
        ("ods", "OpenDocument"),
        ("csv", "первый лист"),
    ];

    private const string NoOffice =
        "Для документов Word и Excel нужен Microsoft Office, бесплатные WordCraft и GridCraft (getartcraft.com/apps) или LibreOffice (libreoffice.org).";

    // Что WordCraft и GridCraft умеют открывать и сохранять (по wordcraft-cli convert и gridcraft-cli convert).
    private static readonly HashSet<string> WordCraftReads = new(["docx", "docm", "dotx", "dotm", "doc", "rtf", "odt"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WordCraftWrites = ["pdf", "docx", "rtf", "odt", "txt"];
    private static readonly HashSet<string> GridCraftReads = new(["xlsx", "xlsm", "xltx", "xltm", "xls", "xlsb", "ods", "csv"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> GridCraftWrites = ["pdf", "xlsx", "csv"];

    // Пароль-заглушка: для защищённого файла Office вместо окна ввода пароля сразу сообщает об ошибке,
    // а на обычные файлы не влияет.
    private const string NoPassword = "\u0001orbit";

    public static Task DocumentAsync(string source, string destination, string target, OfficeEngine engine, IProgress<double> progress, CancellationToken token) =>
        ConvertAsync(source, destination, target, engine, spreadsheet: false, progress, token);

    public static Task SpreadsheetAsync(string source, string destination, string target, OfficeEngine engine, IProgress<double> progress, CancellationToken token) =>
        ConvertAsync(source, destination, target, engine, spreadsheet: true, progress, token);

    /// <summary>Какие программы для документов найдены на компьютере — для подсказки в настройках.</summary>
    public static IReadOnlyList<OfficeEngine> Installed() =>
        new[] { OfficeEngine.MicrosoftOffice, OfficeEngine.ArtCraft, OfficeEngine.LibreOffice }.Where(e => e switch
        {
            OfficeEngine.MicrosoftOffice => Type.GetTypeFromProgID("Word.Application") is not null || Type.GetTypeFromProgID("Excel.Application") is not null,
            OfficeEngine.ArtCraft => FindCraftCli("WordCraft") is not null || FindCraftCli("GridCraft") is not null,
            _ => FindLibreOffice() is not null,
        }).ToList();

    private static async Task ConvertAsync(string source, string destination, string target, OfficeEngine engine, bool spreadsheet,
        IProgress<double> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var ext = Path.GetExtension(source).TrimStart('.');
        var order = engine == OfficeEngine.Auto ? [OfficeEngine.MicrosoftOffice, OfficeEngine.ArtCraft, OfficeEngine.LibreOffice] : new[] { engine };
        string? refusal = null;
        foreach (var candidate in order)
        {
            var run = candidate switch
            {
                OfficeEngine.MicrosoftOffice => MicrosoftOffice(source, destination, target, spreadsheet),
                OfficeEngine.ArtCraft => ArtCraft(source, destination, ext, target, spreadsheet, token, ref refusal),
                _ => FindLibreOffice() is { } soffice ? () => LibreOfficeAsync(soffice, source, destination, target, token) : null,
            };
            if (run is null)
                continue;
            await run();
            progress.Report(1);
            return;
        }
        throw new InvalidOperationException(refusal ?? (engine switch
        {
            OfficeEngine.MicrosoftOffice => $"Microsoft {(spreadsheet ? "Excel" : "Word")} не установлен. Выберите другую программу в настройках Orbit.",
            OfficeEngine.ArtCraft => $"{(spreadsheet ? "GridCraft" : "WordCraft")} не установлен (getartcraft.com/apps). Выберите другую программу в настройках Orbit.",
            OfficeEngine.LibreOffice => "LibreOffice не установлен (libreoffice.org). Выберите другую программу в настройках Orbit.",
            _ => NoOffice,
        }));
    }

    private static Func<Task>? MicrosoftOffice(string source, string destination, string target, bool spreadsheet)
    {
        var type = Type.GetTypeFromProgID(spreadsheet ? "Excel.Application" : "Word.Application");
        if (type is null)
            return null;
        return spreadsheet
            ? () => RunStaAsync(() => WithApplication(type, excel: true, app => ExcelConvert(app, source, destination, target)))
            : () => RunStaAsync(() => WithApplication(type, excel: false, app => WordConvert(app, source, destination, target)));
    }

    private static Func<Task>? ArtCraft(string source, string destination, string ext, string target, bool spreadsheet, CancellationToken token, ref string? refusal)
    {
        var app = spreadsheet ? "GridCraft" : "WordCraft";
        if (FindCraftCli(app) is not { } cli)
            return null;
        var (reads, writes) = spreadsheet ? (GridCraftReads, GridCraftWrites) : (WordCraftReads, WordCraftWrites);
        if (!reads.Contains(ext) || !writes.Contains(target))
        {
            // Запоминаем, почему не подошёл: если других программ нет, это и будет ответом.
            refusal ??= $"{app} не умеет переводить {ext.ToUpperInvariant()} в {target.ToUpperInvariant()}.";
            return null;
        }
        IEnumerable<string> args = spreadsheet && target == "pdf"
            // У convert нет PDF — печатаем все листы командой самого GridCraft (как «Файл → Экспорт в PDF»).
            ? ["run", "--in", source, "--quiet", "--cmd", $"file.exportPdf={JsonSerializer.Serialize(new { path = destination, sheets = "all" })}"]
            : ["convert", source, destination];
        return () => RunToolAsync(app, cli, args, token);
    }

    /// <summary>PDF → DOCX: это умеет только Word (2013 и новее), он распознаёт в PDF абзацы и таблицы.</summary>
    public static async Task PdfToWordAsync(string source, string destination, IProgress<double> progress, CancellationToken token)
    {
        var word = Type.GetTypeFromProgID("Word.Application")
            ?? throw new InvalidOperationException("PDF в Word переводит Microsoft Word — он не установлен.");
        token.ThrowIfCancellationRequested();
        await RunStaAsync(() => WithApplication(word, excel: false, app => WordConvert(app, source, destination, "docx")));
        progress.Report(1);
    }

    // ---------------------------------------------------------------- Microsoft Office

    private static void WithApplication(Type type, bool excel, Action<dynamic> work)
    {
        dynamic app = Activator.CreateInstance(type)!;
        // Office мог подключиться к уже открытому пользователем экземпляру: его не закрываем и возвращаем ему настройки.
        var ours = true;
        object? alerts = null, security = null;
        try
        {
            ours = (excel ? (int)app.Workbooks.Count : (int)app.Documents.Count) == 0;
            alerts = app.DisplayAlerts;
            security = app.AutomationSecurity;
            app.DisplayAlerts = excel ? (object)false : 0; // у Excel это bool, у Word — WdAlertLevel
            app.AutomationSecurity = 3; // msoAutomationSecurityForceDisable: макросы из файла не запускаются
            work(app);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(Explain(ex), ex);
        }
        finally
        {
            try
            {
                if (ours && excel)
                {
                    app.Quit();
                }
                else if (ours)
                {
                    app.Quit(SaveChanges: 0); // wdDoNotSaveChanges
                }
                else
                {
                    if (alerts is not null)
                        app.DisplayAlerts = (dynamic)alerts;
                    if (security is not null)
                        app.AutomationSecurity = (dynamic)security;
                }
            }
            catch (COMException)
            {
            }
            Marshal.FinalReleaseComObject(app);
        }
    }

    private static void WordConvert(dynamic word, string source, string destination, string target)
    {
        word.Visible = false;
        dynamic doc = word.Documents.Open(FileName: source, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
            PasswordDocument: NoPassword, Visible: false, NoEncodingDialog: true);
        try
        {
            // WdSaveFormat; текст — в UTF-8 (кодовая страница 65001).
            if (target == "txt")
                doc.SaveAs2(FileName: destination, FileFormat: 2, AddToRecentFiles: false, Encoding: 65001);
            else
                doc.SaveAs2(FileName: destination, FileFormat: target switch
                {
                    "pdf" => 17,
                    "docx" => 16,
                    "doc" => 0,
                    "rtf" => 6,
                    "odt" => 23,
                    _ => throw new ArgumentOutOfRangeException(nameof(target)),
                }, AddToRecentFiles: false);
        }
        finally
        {
            doc.Close(SaveChanges: false);
            Marshal.FinalReleaseComObject(doc);
        }
    }

    private static void ExcelConvert(dynamic excel, string source, string destination, string target)
    {
        excel.Visible = false;
        excel.ScreenUpdating = false;
        // Local: CSV читается и пишется с разделителем из региональных настроек (в России — «;»), как при открытии двойным щелчком.
        dynamic book = excel.Workbooks.Open(Filename: source, UpdateLinks: 0, ReadOnly: true, Password: NoPassword,
            IgnoreReadOnlyRecommended: true, AddToMru: false, Local: true);
        try
        {
            if (target == "pdf")
            {
                book.ExportAsFixedFormat(Type: 0, Filename: destination, IgnorePrintAreas: false, OpenAfterPublish: false);
                return;
            }
            if (target == "csv")
                book.Worksheets[1].Activate(); // в CSV попадает только активный лист
            book.SaveAs(Filename: destination, FileFormat: target switch
            {
                "xlsx" => 51,
                "xls" => 56,
                "ods" => 60,
                "csv" => 62, // CSV UTF-8
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            }, AddToMru: false, Local: true);
        }
        finally
        {
            book.Close(SaveChanges: false);
            Marshal.FinalReleaseComObject(book);
        }
    }

    private static string Explain(COMException ex) => ex.Message switch
    {
        var m when m.Contains("password", StringComparison.OrdinalIgnoreCase) || m.Contains("пароль", StringComparison.OrdinalIgnoreCase)
            => "Файл защищён паролем.",
        var m when !string.IsNullOrWhiteSpace(m) => m.Trim(),
        _ => $"Office не смог обработать файл (код 0x{ex.HResult:X8}).",
    };

    /// <summary>Объекты Office работают только в однопоточном апартаменте (STA).</summary>
    private static Task RunStaAsync(Action work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                work();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Orbit Office",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    // ---------------------------------------------------------------- ArtCraft (WordCraft, GridCraft)

    /// <summary>
    /// wordcraft-cli.exe / gridcraft-cli.exe: рядом с программой, которую установщик прописывает в App Paths
    /// (по умолчанию Program Files\WordCraft), или в PATH — для переносной версии из zip.
    /// </summary>
    private static string? FindCraftCli(string app)
    {
        var name = app.ToLowerInvariant();
        var folders = new List<string?>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{name}.exe");
            if (key?.GetValue(null) is string exe)
                folders.Add(Path.GetDirectoryName(exe.Trim('"')));
        }
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), app));
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", app));
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        return folders.Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.Combine(f!.Trim(), $"{name}-cli.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Запускает консольную программу без окна; если она завершилась с ошибкой — показывает её сообщение.</summary>
    private static async Task RunToolAsync(string name, string exe, IEnumerable<string> args, CancellationToken token)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Не удалось запустить {name}.");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        await output;
        var message = await errors;
        if (process.ExitCode != 0)
        {
            // Последняя строка — сама ошибка; выше бывают предупреждения об импорте.
            var last = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            throw new InvalidOperationException(last is { Length: > 0 } ? $"{name}: {last}" : $"{name} не смог сконвертировать файл (код {process.ExitCode}).");
        }
    }

    // ---------------------------------------------------------------- LibreOffice

    private static string? FindLibreOffice() =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "LibreOffice", "program", "soffice.exe"))
            .FirstOrDefault(File.Exists);

    private static async Task LibreOfficeAsync(string soffice, string source, string destination, string target, CancellationToken token)
    {
        var filter = target switch
        {
            "txt" => "txt:Text (encoded):UTF8",
            "csv" => "csv:Text - txt - csv (StarCalc):59,34,76", // «;», кавычки, UTF-8
            _ => target,
        };
        var work = Path.Combine(Path.GetTempPath(), $"orbit-lo-{Guid.NewGuid():N}");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(outDir);
        try
        {
            // Отдельный профиль: иначе запуск передаётся уже открытому LibreOffice пользователя и тут же завершается.
            var profile = new Uri(Path.Combine(work, "profile")).AbsoluteUri;
            await RunToolAsync("LibreOffice", soffice,
                [$"-env:UserInstallation={profile}", "--headless", "--norestore", "--convert-to", filter, "--outdir", outDir, source], token);
            var result = Directory.EnumerateFiles(outDir).FirstOrDefault()
                ?? throw new InvalidOperationException("LibreOffice не смог сконвертировать файл.");
            File.Move(result, destination, overwrite: true);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
