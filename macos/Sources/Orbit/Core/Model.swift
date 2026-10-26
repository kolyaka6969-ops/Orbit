import Foundation

/// Три круговых меню: Shift — конвертация, Control — инструменты, Option — действия с файлами.
enum WheelKind: Int, CaseIterable {
    case convert
    case tools
    case actions

    var title: String {
        switch self {
        case .convert: return "Конвертация"
        case .tools: return "Инструменты"
        case .actions: return "Действия"
        }
    }

    var keyName: String {
        switch self {
        case .convert: return "⇧ Shift"
        case .tools: return "⌃ Control"
        case .actions: return "⌥ Option"
        }
    }

    var symbol: String {
        switch self {
        case .convert: return "arrow.triangle.2.circlepath"
        case .tools: return "wrench.and.screwdriver"
        case .actions: return "square.grid.2x2"
        }
    }
}

enum ModifierKey {
    case shift
    case control
    case option
}

enum FileKind {
    case other
    case folder
    case image
    case raw
    case audio
    case video
    case pdf
    case archive
    case document
    case spreadsheet
}

enum Formats {
    static let images = set("png jpg jpeg jfif jpe webp avif heic heif bmp gif tif tiff ico icns tga psd jxl jp2 exr")

    /// RAW с фотокамер (их декодирует ImageIO).
    static let raw = set("cr2 cr3 crw nef nrw arw srf sr2 dng raf orf rw2 pef srw x3f 3fr erf kdc mrw iiq")

    /// То, что читает AVFoundation.
    static let audio = set("mp3 m4a m4b aac wav flac aif aiff aifc caf amr ac3")

    static let video = set("mp4 m4v mov 3gp 3g2")

    /// Документы Word и совместимые с ним.
    static let documents = set("docx doc docm dotx dot dotm rtf odt")

    /// Таблицы Excel и совместимые с ним.
    static let spreadsheets = set("xlsx xls xlsm xlsb xltx xlt xltm ods csv")

    static let archives = set("zip 7z rar tar gz tgz bz2 tbz2 xz txz")

    static func kind(of ext: String) -> FileKind {
        if images.contains(ext) { return .image }
        if raw.contains(ext) { return .raw }
        if audio.contains(ext) { return .audio }
        if video.contains(ext) { return .video }
        if ext == "pdf" { return .pdf }
        if archives.contains(ext) { return .archive }
        if documents.contains(ext) { return .document }
        if spreadsheets.contains(ext) { return .spreadsheet }
        return .other
    }

    /// Одно имя для синонимов: jpeg → jpg, tif → tiff.
    static func canonical(_ ext: String) -> String {
        switch ext {
        case "jpeg", "jfif", "jpe": return "jpg"
        case "tif": return "tiff"
        case "heif": return "heic"
        case "aif", "aifc": return "aiff"
        default: return ext
        }
    }

    static func isPicture(_ item: FileItem) -> Bool { item.kind == .image || item.kind == .raw }

    private static func set(_ list: String) -> Set<String> { Set(list.split(separator: " ").map(String.init)) }
}

struct FileItem {
    let url: URL
    let isDirectory: Bool
    /// Расширение без точки, в нижнем регистре.
    let ext: String
    let kind: FileKind

    init(url: URL) {
        self.url = url.standardizedFileURL
        var isDir: ObjCBool = false
        FileManager.default.fileExists(atPath: self.url.path, isDirectory: &isDir)
        isDirectory = isDir.boolValue
        ext = isDirectory ? "" : self.url.pathExtension.lowercased()
        kind = isDirectory ? .folder : Formats.kind(of: ext)
    }

    var path: String { url.path }

    var name: String { url.lastPathComponent }

    /// Имя без расширения; для «.tar.gz» отрезаются оба.
    var baseName: String {
        if isDirectory { return name }
        let name = url.deletingPathExtension().lastPathComponent
        return name.lowercased().hasSuffix(".tar") ? String(name.dropLast(4)) : name
    }

    var folder: URL { url.deletingLastPathComponent() }
}

struct OrbitError: LocalizedError {
    let message: String

    init(_ message: String) { self.message = message }

    var errorDescription: String? { message }
}

/// Всё, что нужно операции во время работы. Создаётся на каждую задачу.
final class JobContext: @unchecked Sendable {
    let settings: SettingsSnapshot
    private let report: @Sendable (Double) -> Void
    private let lock = NSLock()
    private var last = -1.0
    private var _message: String?

    init(settings: SettingsSnapshot, report: @escaping @Sendable (Double) -> Void) {
        self.settings = settings
        self.report = report
    }

    /// Текст вместо стандартного «Готово» (например, «Путь скопирован»).
    var message: String? {
        get { lock.lock(); defer { lock.unlock() }; return _message }
        set { lock.lock(); _message = newValue; lock.unlock() }
    }

    /// Прогресс 0…1. Мелкие изменения не отправляются в интерфейс.
    func progress(_ value: Double) {
        let v = min(max(value, 0), 1)
        lock.lock()
        let send = v >= 1 || v - last >= 0.005 || v < last
        if send { last = v }
        lock.unlock()
        if send { report(v) }
    }

    func outputDir(for item: FileItem) -> URL {
        switch settings.output {
        case .subfolder:
            return item.folder.appendingPathComponent("Orbit", isDirectory: true)
        case .custom where !settings.customFolder.isEmpty:
            return URL(fileURLWithPath: settings.customFolder, isDirectory: true)
        default:
            return item.folder
        }
    }
}

/// Пункт кругового меню.
final class Operation {
    let id: String
    let wheel: WheelKind
    let label: String
    /// Вторая строка под названием, например «только звук».
    let subtitle: String?
    /// Имя SF Symbol.
    let symbol: String
    /// Мгновенное действие (копирование пути и т. п.): без полосы прогресса.
    let instant: Bool
    let appliesTo: ([FileItem]) -> Bool
    /// Выполняет операцию и возвращает созданные или изменённые файлы.
    let run: ([FileItem], JobContext) async throws -> [URL]

    init(id: String, wheel: WheelKind, label: String, subtitle: String? = nil, symbol: String, instant: Bool = false,
         appliesTo: @escaping ([FileItem]) -> Bool, run: @escaping ([FileItem], JobContext) async throws -> [URL]) {
        self.id = id
        self.wheel = wheel
        self.label = label
        self.subtitle = subtitle
        self.symbol = symbol
        self.instant = instant
        self.appliesTo = appliesTo
        self.run = run
    }
}

/// «1 файл», «2 файла», «5 файлов».
func plural(_ n: Int, _ one: String, _ few: String, _ many: String) -> String {
    let m10 = n % 10
    let m100 = n % 100
    if m10 == 1 && m100 != 11 { return one }
    if (2...4).contains(m10) && !(12...14).contains(m100) { return few }
    return many
}

func filesCaption(_ items: [FileItem]) -> String {
    items.count == 1 ? items[0].name : "\(items.count) \(plural(items.count, "файл", "файла", "файлов"))"
}
