import AppKit

/// Документы Word и таблицы Excel. Документы умеет конвертировать сама macOS (textutil и печать в PDF),
/// таблицы и всё остальное — LibreOffice, если он установлен.
enum OfficeBackend {
    static let documentTargets: [(ext: String, subtitle: String?)] = [
        ("pdf", "документ"),
        ("docx", "Word"),
        ("doc", "Word 97–2004"),
        ("rtf", nil),
        ("odt", "OpenDocument"),
        ("txt", "только текст"),
    ]

    static let spreadsheetTargets: [(ext: String, subtitle: String?)] = [
        ("pdf", "все листы"),
        ("xlsx", "Excel"),
        ("xls", "Excel 97–2004"),
        ("ods", "OpenDocument"),
        ("csv", "первый лист"),
    ]

    /// Что читает встроенный textutil.
    private static let builtInInputs: Set<String> = ["docx", "doc", "rtf", "odt"]

    static var libreOffice: URL? {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        return ["/Applications/LibreOffice.app/Contents/MacOS/soffice", home + "/Applications/LibreOffice.app/Contents/MacOS/soffice"]
            .first { FileManager.default.isExecutableFile(atPath: $0) }
            .map { URL(fileURLWithPath: $0) }
    }

    private static func useLibreOffice(_ engine: OfficeEngine) -> Bool {
        engine != .builtIn && libreOffice != nil
    }

    static func canConvertDocument(_ item: FileItem, engine: OfficeEngine) -> Bool {
        useLibreOffice(engine) || (engine != .libreOffice && builtInInputs.contains(item.ext))
    }

    static func canConvertSpreadsheet(engine: OfficeEngine) -> Bool { useLibreOffice(engine) }

    static func document(_ src: URL, ext: String, to dst: URL, target: String, engine: OfficeEngine) async throws {
        if useLibreOffice(engine) {
            try await convertWithLibreOffice(src, to: dst, filter: filter(for: target, spreadsheet: false))
        } else if target == "pdf" {
            try await MainActor.run { try printToPdf(src, to: dst) }
        } else {
            var args = ["-convert", target, "-output", dst.path]
            if target == "txt" { args += ["-encoding", "UTF-8"] }
            try await ProcessRunner.run("/usr/bin/textutil", args + [src.path])
        }
    }

    static func spreadsheet(_ src: URL, to dst: URL, target: String, engine: OfficeEngine) async throws {
        guard useLibreOffice(engine) else { throw OrbitError("Для таблиц нужен LibreOffice — установите его с libreoffice.org.") }
        try await convertWithLibreOffice(src, to: dst, filter: filter(for: target, spreadsheet: true))
    }

    private static func filter(for target: String, spreadsheet: Bool) -> String {
        switch target {
        case "docx": return "docx:MS Word 2007 XML"
        case "doc": return "doc:MS Word 97"
        case "txt": return "txt:Text (encoded):UTF8"
        case "xlsx": return "xlsx:Calc MS Excel 2007 XML"
        case "xls": return "xls:MS Excel 97"
        case "csv": return "csv:Text - txt - csv (StarCalc):44,34,76,1"
        default: return target
        }
    }

    private static func convertWithLibreOffice(_ src: URL, to dst: URL, filter: String) async throws {
        guard let soffice = libreOffice else { throw OrbitError("LibreOffice не найден — установите его с libreoffice.org.") }
        let fm = FileManager.default
        let work = fm.temporaryDirectory.appendingPathComponent("orbit-lo-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: work, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: work) }
        // Свой профиль, чтобы не мешать уже открытому LibreOffice.
        let profile = fm.temporaryDirectory.appendingPathComponent("orbit-lo-profile", isDirectory: true)
        try await ProcessRunner.run(soffice.path, [
            "-env:UserInstallation=\(profile.absoluteString)", "--headless", "--norestore", "--nologo",
            "--convert-to", filter, "--outdir", work.path, src.path,
        ])
        guard let produced = try fm.contentsOfDirectory(at: work, includingPropertiesForKeys: nil).first else {
            throw OrbitError("LibreOffice не создал файл.")
        }
        try fm.moveItem(at: produced, to: dst)
    }

    /// Документ в PDF средствами AppKit: текст раскладывается по страницам A4 и «печатается» в файл.
    @MainActor
    private static func printToPdf(_ src: URL, to dst: URL) throws {
        let text = try NSAttributedString(url: src, options: [:], documentAttributes: nil)
        let info = NSPrintInfo()
        info.paperSize = NSSize(width: 595.2, height: 841.8)
        info.topMargin = 56
        info.bottomMargin = 56
        info.leftMargin = 56
        info.rightMargin = 56
        info.horizontalPagination = .fit
        info.verticalPagination = .automatic
        info.isHorizontallyCentered = false
        info.isVerticallyCentered = false
        info.jobDisposition = .save
        info.dictionary()[NSPrintInfo.AttributeKey.jobSavingURL.rawValue] = dst

        let width = info.paperSize.width - info.leftMargin - info.rightMargin
        let view = NSTextView(frame: NSRect(x: 0, y: 0, width: width, height: 100))
        view.appearance = NSAppearance(named: .aqua)
        view.backgroundColor = .white
        view.textContainerInset = .zero
        view.textContainer?.lineFragmentPadding = 0
        view.isVerticallyResizable = true
        view.textStorage?.setAttributedString(text)
        if let layout = view.layoutManager, let container = view.textContainer {
            layout.ensureLayout(for: container)
            view.frame.size.height = max(layout.usedRect(for: container).height, 100)
        }

        let operation = NSPrintOperation(view: view, printInfo: info)
        operation.showsPrintPanel = false
        operation.showsProgressPanel = false
        guard operation.run() else { throw OrbitError("Не удалось сохранить PDF.") }
    }
}
