import AppKit

/// Имена SF Symbols для пунктов меню.
enum Symbols {
    static let photo = "photo"
    static let audio = "waveform"
    static let video = "film"
    static let document = "doc.text"
    static let pdf = "doc.richtext"
    static let zip = "doc.zipper"
    static let extract = "archivebox"
    static let shrink = "arrow.down.right.and.arrow.up.left"
    static let rotateRight = "rotate.right"
    static let rotateLeft = "rotate.left"
    static let mirror = "arrow.left.and.right.righttriangle.left.righttriangle.right"
    static let color = "circle.lefthalf.filled"
    static let clear = "eye.slash"
    static let crop = "crop"
    static let text = "text.viewfinder"
    static let merge = "arrow.triangle.merge"
    static let split = "square.split.2x1"
    static let mute = "speaker.slash"
    static let hash = "number"
    static let link = "link"
    static let paste = "doc.on.clipboard"
    static let desktop = "menubar.dock.rectangle"
    static let move = "folder"
    static let copy = "plus.square.on.square"
    static let trash = "trash"
    static let more = "ellipsis"
    static let info = "info.circle"
}

/// Все пункты трёх круговых меню. Порядок — по часовой стрелке от «12 часов».
enum Catalog {
    static func build() -> [Operation] {
        var ops: [Operation] = []
        addImages(&ops)
        addMedia(&ops)
        addPdf(&ops)
        addOffice(&ops)
        addFiles(&ops)
        addActions(&ops)
        return ops
    }

    private static func sorted(_ items: [FileItem]) -> [FileItem] {
        items.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    // MARK: изображения

    private static func addImages(_ ops: inout [Operation]) {
        for target in ImageBackend.targets {
            ops.append(perFile("image.to.\(target.ext)", .convert, target.ext.uppercased(), target.subtitle, Symbols.photo,
                               { Formats.isPicture($0) && Formats.canonical($0.ext) != target.ext }) { item, ctx in
                [try await OutputPaths.produce(ctx.outputDir(for: item), item.baseName, target.ext) { tmp in
                    try ImageBackend.convert(item.url, to: tmp, ext: target.ext, quality: Double(ctx.settings.imageQuality) / 100)
                }]
            })
        }

        ops.append(perFile("image.compress", .tools, "Сжать", "меньше вес", Symbols.shrink, Formats.isPicture) { item, ctx in
            let format = ImageBackend.compressFormat(for: item)
            return [try await OutputPaths.produce(ctx.outputDir(for: item), "\(item.baseName) (сжато)", format) { tmp in
                try ImageBackend.compress(item.url, to: tmp, ext: format)
            }]
        })
        imageEdit(&ops, "image.half", "Уменьшить", "50 %", Symbols.shrink, "50%") { try ImageBackend.scale($0, by: 0.5) }
        imageEdit(&ops, "image.web", "Для интернета", "до 1920 px", Symbols.shrink, "1920") { try ImageBackend.fit($0, max: 1920) }
        imageEdit(&ops, "image.rotate.right", "Повернуть", "вправо", Symbols.rotateRight, "повёрнуто") { try ImageBackend.rotate($0, clockwise: true) }
        imageEdit(&ops, "image.rotate.left", "Повернуть", "влево", Symbols.rotateLeft, "повёрнуто") { try ImageBackend.rotate($0, clockwise: false) }
        imageEdit(&ops, "image.mirror", "Отразить", "зеркально", Symbols.mirror, "отражено") { try ImageBackend.mirror($0) }
        imageEdit(&ops, "image.gray", "Ч/б", "серый", Symbols.color, "ч-б") { try ImageBackend.grayscale($0) }
        imageEdit(&ops, "image.square", "Квадрат", "обрезка", Symbols.crop, "квадрат") { try ImageBackend.cropSquare($0) }
        imageEdit(&ops, "image.strip", "Без метаданных", "EXIF, GPS", Symbols.clear, "без метаданных", keepMetadata: false) { $0 }

        ops.append(perFile("image.ocr", .tools, "Текст", "распознать", Symbols.text, Formats.isPicture) { item, ctx in
            let text = try OcrBackend.recognize(item.url)
            if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                throw OrbitError("Текст на изображении не найден.")
            }
            await MainActor.run { Clipboard.setText(text) }
            ctx.message = "Текст скопирован в буфер обмена"
            return [try await OutputPaths.produce(ctx.outputDir(for: item), item.baseName, "txt") { tmp in
                try text.write(to: tmp, atomically: false, encoding: .utf8)
            }]
        })

        ops.append(Operation(id: "image.pdf", wheel: .tools, label: "В один PDF", subtitle: "по странице", symbol: Symbols.pdf,
                             appliesTo: { $0.count >= 2 && $0.allSatisfy(Formats.isPicture) }) { items, ctx in
            let list = sorted(items)
            return [try await OutputPaths.produce(ctx.outputDir(for: list[0]), list[0].baseName, "pdf") { tmp in
                try ImageBackend.imagesToPdf(list.map(\.url), to: tmp, progress: ctx.progress)
            }]
        })
    }

    private static func imageEdit(_ ops: inout [Operation], _ id: String, _ label: String, _ subtitle: String, _ symbol: String,
                                  _ suffix: String, keepMetadata: Bool = true, _ transform: @escaping (CGImage) throws -> CGImage) {
        ops.append(perFile(id, .tools, label, subtitle, symbol, Formats.isPicture) { item, ctx in
            let format = ImageBackend.editFormat(for: item)
            return [try await OutputPaths.produce(ctx.outputDir(for: item), "\(item.baseName) (\(suffix))", format) { tmp in
                try ImageBackend.edit(item.url, to: tmp, ext: format, quality: Double(ctx.settings.imageQuality) / 100,
                                      keepMetadata: keepMetadata, transform)
            }]
        })
    }

    // MARK: аудио и видео

    private static func addMedia(_ ops: inout [Operation]) {
        for target in MediaBackend.audioTargets {
            ops.append(perFile("audio.to.\(target.ext)", .convert, target.ext.uppercased(), target.subtitle, Symbols.audio,
                               { $0.kind == .audio && Formats.canonical($0.ext) != target.ext }) { item, ctx in
                [try await OutputPaths.produce(ctx.outputDir(for: item), item.baseName, target.ext) { tmp in
                    try await MediaBackend.toAudio(item.url, tmp, ext: target.ext, bitrate: 256_000, progress: ctx.progress)
                }]
            })
        }
        ops.append(perFile("audio.compress", .tools, "Сжать", "AAC 128 кбит/с", Symbols.shrink, { $0.kind == .audio }) { item, ctx in
            [try await OutputPaths.produce(ctx.outputDir(for: item), "\(item.baseName) (сжато)", "m4a") { tmp in
                try await MediaBackend.toAudio(item.url, tmp, ext: "m4a", bitrate: 128_000, progress: ctx.progress)
            }]
        })

        for target in MediaBackend.videoTargets {
            ops.append(perFile("video.to.\(target.ext)", .convert, target.ext.uppercased(), target.subtitle, Symbols.video,
                               { $0.kind == .video && Formats.canonical($0.ext) != target.ext }) { item, ctx in
                [try await OutputPaths.produce(ctx.outputDir(for: item), item.baseName, target.ext) { tmp in
                    try await MediaBackend.exportVideo(item.url, tmp, ext: target.ext, preset: MediaBackend.preset(for: ctx.settings.video),
                                                       progress: ctx.progress)
                }]
            })
        }
        for target in ["m4a", "wav"] {
            ops.append(perFile("video.audio.\(target)", .convert, target.uppercased(), "только звук", Symbols.audio, { $0.kind == .video }) { item, ctx in
                [try await OutputPaths.produce(ctx.outputDir(for: item), item.baseName, target) { tmp in
                    try await MediaBackend.toAudio(item.url, tmp, ext: target, bitrate: 256_000, progress: ctx.progress)
                }]
            })
        }

        video(&ops, "video.compress", "Сжать", "HEVC", Symbols.shrink, "сжато") { _ in (AVPreset.hevc1080, false) }
        video(&ops, "video.720", "720p", "уменьшить", Symbols.shrink, "720p") { _ in (AVPreset.p720, false) }
        video(&ops, "video.mute", "Без звука", "убрать дорожку", Symbols.mute, "без звука") { s in (MediaBackend.preset(for: s.video), true) }
    }

    private enum AVPreset {
        static let hevc1080 = "AVAssetExportPresetHEVC1920x1080"
        static let p720 = "AVAssetExportPreset1280x720"
    }

    private static func video(_ ops: inout [Operation], _ id: String, _ label: String, _ subtitle: String, _ symbol: String,
                              _ suffix: String, _ options: @escaping (SettingsSnapshot) -> (preset: String, mute: Bool)) {
        ops.append(perFile(id, .tools, label, subtitle, symbol, { $0.kind == .video }) { item, ctx in
            let o = options(ctx.settings)
            return [try await OutputPaths.produce(ctx.outputDir(for: item), "\(item.baseName) (\(suffix))", "mp4") { tmp in
                try await MediaBackend.exportVideo(item.url, tmp, ext: "mp4", preset: o.preset, mute: o.mute, progress: ctx.progress)
            }]
        })
    }

    // MARK: PDF

    private static func addPdf(_ ops: inout [Operation]) {
        for target in ["png", "jpg"] {
            ops.append(perFile("pdf.to.\(target)", .convert, target.uppercased(), "страницы", Symbols.photo, { $0.kind == .pdf }) { item, ctx in
                try await PdfBackend.toImages(item, dir: ctx.outputDir(for: item), ext: target, dpi: ctx.settings.pdfDpi, progress: ctx.progress)
            })
        }
        ops.append(perFile("pdf.to.txt", .convert, "TXT", "только текст", Symbols.document, { $0.kind == .pdf }) { item, ctx in
            [try await OutputPaths.produce(ctx.outputDir(for: item), item.baseName, "txt") { tmp in
                try PdfBackend.toText(item.url, to: tmp)
            }]
        })
        ops.append(Operation(id: "pdf.merge", wheel: .tools, label: "Объединить", subtitle: "в один PDF", symbol: Symbols.merge,
                             appliesTo: { $0.count >= 2 && $0.allSatisfy { $0.kind == .pdf } }) { items, ctx in
            let list = sorted(items)
            return [try await OutputPaths.produce(ctx.outputDir(for: list[0]), "\(list[0].baseName) (объединено)", "pdf") { tmp in
                try PdfBackend.merge(list.map(\.url), to: tmp, progress: ctx.progress)
            }]
        })
        ops.append(perFile("pdf.split", .tools, "Разделить", "по страницам", Symbols.split, { $0.kind == .pdf }) { item, ctx in
            try await PdfBackend.split(item, dir: ctx.outputDir(for: item), progress: ctx.progress)
        })
    }

    // MARK: Word и Excel

    private static func addOffice(_ ops: inout [Operation]) {
        for target in OfficeBackend.documentTargets {
            ops.append(perFile("document.to.\(target.ext)", .convert, target.ext.uppercased(), target.subtitle,
                               target.ext == "pdf" ? Symbols.pdf : Symbols.document, { item in
                item.kind == .document && item.ext != target.ext
                    && OfficeBackend.canConvertDocument(item, engine: SettingsSnapshot.current().office)
            }) { item, ctx in
                [try await OutputPaths.produce(ctx.outputDir(for: item), item.baseName, target.ext) { tmp in
                    try await OfficeBackend.document(item.url, ext: item.ext, to: tmp, target: target.ext, engine: ctx.settings.office)
                }]
            })
        }
        for target in OfficeBackend.spreadsheetTargets {
            ops.append(perFile("spreadsheet.to.\(target.ext)", .convert, target.ext.uppercased(), target.subtitle,
                               target.ext == "pdf" ? Symbols.pdf : Symbols.document, { item in
                item.kind == .spreadsheet && item.ext != target.ext
                    && OfficeBackend.canConvertSpreadsheet(engine: SettingsSnapshot.current().office)
            }) { item, ctx in
                [try await OutputPaths.produce(ctx.outputDir(for: item), item.baseName, target.ext) { tmp in
                    try await OfficeBackend.spreadsheet(item.url, to: tmp, target: target.ext, engine: ctx.settings.office)
                }]
            })
        }
    }

    // MARK: архивы и любые файлы

    private static func addFiles(_ ops: inout [Operation]) {
        ops.append(perFile("archive.extract", .convert, "Распаковать", "в папку", Symbols.extract, { $0.kind == .archive }) { item, ctx in
            [try await ArchiveBackend.extract(item, dir: ctx.outputDir(for: item))]
        })

        let zip: ([FileItem], JobContext) async throws -> [URL] = { items, ctx in
            let folderName = items[0].folder.lastPathComponent
            let name = items.count == 1 ? items[0].baseName : (folderName.isEmpty || folderName == "/" ? "Архив" : folderName)
            return [try await OutputPaths.produce(ctx.outputDir(for: items[0]), name, "zip") { tmp in
                try await ArchiveBackend.zip(items, to: tmp)
            }]
        }
        // На колесе конвертации ZIP есть для того, что больше ни во что не конвертируется (папки, прочие файлы).
        ops.append(Operation(id: "convert.zip", wheel: .convert, label: "ZIP", subtitle: "архив", symbol: Symbols.zip,
                             appliesTo: { !$0.isEmpty && $0.allSatisfy { $0.kind == .folder || $0.kind == .other } }, run: zip))
        ops.append(Operation(id: "tools.zip", wheel: .tools, label: "ZIP", subtitle: "архив", symbol: Symbols.zip,
                             appliesTo: { !$0.isEmpty }, run: zip))
        ops.append(Operation(id: "tools.sha256", wheel: .tools, label: "SHA-256", subtitle: "контр. сумма", symbol: Symbols.hash,
                             appliesTo: { !$0.isEmpty && $0.allSatisfy { !$0.isDirectory } }) { items, ctx in
            var lines: [String] = []
            for (i, item) in items.enumerated() {
                let hex = try ArchiveBackend.sha256(item.url)
                lines.append(items.count == 1 ? hex : "\(hex)  \(item.name)")
                ctx.progress(Double(i + 1) / Double(items.count))
            }
            let text = lines.joined(separator: "\n")
            await MainActor.run { Clipboard.setText(text) }
            ctx.message = "Контрольная сумма скопирована"
            return []
        })
    }

    // MARK: действия (Option)

    private static func addActions(_ ops: inout [Operation]) {
        ops.append(action("action.copy.path", "Путь", "копировать", Symbols.link) { items, ctx in
            let text = items.map(\.path).joined(separator: "\n")
            await MainActor.run { Clipboard.setText(text) }
            ctx.message = items.count == 1 ? "Путь скопирован" : "Пути скопированы"
            return []
        })
        ops.append(action("action.copy.clipboard", "В буфер", "как файлы", Symbols.paste) { items, ctx in
            let urls = items.map(\.url)
            await MainActor.run { Clipboard.setFiles(urls) }
            ctx.message = "Файлы в буфере обмена — вставьте их через ⌘V"
            return []
        })
        ops.append(action("action.desktop", "На рабочий стол", "копия", Symbols.desktop, instant: false) { items, _ in
            let desktop = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask)[0]
            return try items.map { try copy($0, to: desktop, baseName: $0.baseName) }
        })
        ops.append(action("action.move", "Переместить", "в папку…", Symbols.move, instant: false) { items, _ in
            let target: URL? = await MainActor.run {
                NSApp.activate(ignoringOtherApps: true)
                let panel = NSOpenPanel()
                panel.canChooseDirectories = true
                panel.canChooseFiles = false
                panel.canCreateDirectories = true
                panel.prompt = "Переместить"
                panel.message = "Куда переместить файлы?"
                return panel.runModal() == .OK ? panel.url : nil
            }
            guard let target else { throw CancellationError() }
            return try items.map { item in
                let destination = OutputPaths.unique(target, item.baseName, item.ext)
                try FileManager.default.moveItem(at: item.url, to: destination)
                return destination
            }
        })
        ops.append(action("action.duplicate", "Копия", "рядом", Symbols.copy, instant: false) { items, _ in
            try items.map { try copy($0, to: $0.folder, baseName: "\($0.baseName) (копия)") }
        })
        ops.append(action("action.trash", "В корзину", "можно вернуть", Symbols.trash) { items, ctx in
            for item in items {
                try FileManager.default.trashItem(at: item.url, resultingItemURL: nil)
            }
            ctx.message = items.count == 1 ? "Перемещено в корзину" : "В корзине: \(items.count)"
            return []
        })
    }

    private static func copy(_ item: FileItem, to dir: URL, baseName: String) throws -> URL {
        let destination = OutputPaths.unique(dir, baseName, item.ext)
        try FileManager.default.copyItem(at: item.url, to: destination)
        return destination
    }

    // MARK: помощники

    private static func action(_ id: String, _ label: String, _ subtitle: String, _ symbol: String, instant: Bool = true,
                               _ run: @escaping ([FileItem], JobContext) async throws -> [URL]) -> Operation {
        Operation(id: id, wheel: .actions, label: label, subtitle: subtitle, symbol: symbol, instant: instant,
                  appliesTo: { !$0.isEmpty }, run: run)
    }

    /// Операция, которая выполняется для каждого файла по очереди.
    private static func perFile(_ id: String, _ wheel: WheelKind, _ label: String, _ subtitle: String?, _ symbol: String,
                                _ accepts: @escaping (FileItem) -> Bool,
                                _ run: @escaping (FileItem, JobContext) async throws -> [URL]) -> Operation {
        Operation(id: id, wheel: wheel, label: label, subtitle: subtitle, symbol: symbol,
                  appliesTo: { !$0.isEmpty && $0.allSatisfy(accepts) }) { items, ctx in
            var outputs: [URL] = []
            for (i, item) in items.enumerated() {
                try Task.checkCancellation()
                let part = JobContext(settings: ctx.settings) { ctx.progress((Double(i) + $0) / Double(items.count)) }
                outputs += try await run(item, part)
                if let message = part.message { ctx.message = message }
                ctx.progress(Double(i + 1) / Double(items.count))
            }
            return outputs
        }
    }
}
