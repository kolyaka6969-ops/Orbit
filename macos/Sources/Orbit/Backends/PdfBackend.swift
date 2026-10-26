import Foundation
import PDFKit

/// PDF через PDFKit и Core Graphics.
enum PdfBackend {
    static func open(_ url: URL) throws -> PDFDocument {
        guard let document = PDFDocument(url: url) else { throw OrbitError("Не удалось открыть PDF.") }
        if document.isLocked { throw OrbitError("PDF защищён паролем.") }
        return document
    }

    /// Страницы в картинки: одна страница — один файл рядом, несколько — папка с именем документа.
    static func toImages(_ item: FileItem, dir: URL, ext: String, dpi: Int, progress: @escaping (Double) -> Void) async throws -> [URL] {
        let document = try open(item.url)
        let count = document.pageCount
        guard count > 0 else { throw OrbitError("В PDF нет страниц.") }
        let scale = CGFloat(dpi) / 72
        if count == 1, let page = document.page(at: 0) {
            return [try await OutputPaths.produce(dir, item.baseName, ext) { tmp in
                try render(page, scale: scale, ext: ext, to: tmp)
            }]
        }
        let digits = String(count).count
        return [try await OutputPaths.produceFolder(dir, item.baseName) { folder in
            for i in 0..<count {
                try Task.checkCancellation()
                guard let page = document.page(at: i) else { continue }
                let number = String(repeating: "0", count: digits - String(i + 1).count) + String(i + 1)
                try render(page, scale: scale, ext: ext, to: folder.appendingPathComponent("\(item.baseName) \(number).\(ext)"))
                progress(Double(i + 1) / Double(count))
            }
        }]
    }

    private static func render(_ page: PDFPage, scale: CGFloat, ext: String, to url: URL) throws {
        guard let cgPage = page.pageRef else { throw OrbitError("Не удалось прочитать страницу.") }
        let box = cgPage.getBoxRect(.cropBox)
        let turned = cgPage.rotationAngle % 180 != 0
        let size = turned ? CGSize(width: box.height, height: box.width) : box.size
        let image = try ImageBackend.render(width: Int((size.width * scale).rounded()), height: Int((size.height * scale).rounded()), opaque: true) { ctx in
            ctx.scaleBy(x: scale, y: scale)
            ctx.concatenate(cgPage.getDrawingTransform(.cropBox, rect: CGRect(origin: .zero, size: size), rotate: 0, preserveAspectRatio: true))
            ctx.drawPDFPage(cgPage)
        }
        try ImageBackend.write(image, to: url, ext: ext, quality: 0.9, metadata: nil)
    }

    static func merge(_ urls: [URL], to dst: URL, progress: @escaping (Double) -> Void) throws {
        let result = PDFDocument()
        for (i, url) in urls.enumerated() {
            try Task.checkCancellation()
            let document = try open(url)
            for p in 0..<document.pageCount {
                if let page = document.page(at: p)?.copy() as? PDFPage {
                    result.insert(page, at: result.pageCount)
                }
            }
            progress(Double(i + 1) / Double(urls.count))
        }
        guard result.write(to: dst) else { throw OrbitError("Не удалось сохранить PDF.") }
    }

    static func split(_ item: FileItem, dir: URL, progress: @escaping (Double) -> Void) async throws -> [URL] {
        let document = try open(item.url)
        let count = document.pageCount
        guard count > 1 else { throw OrbitError("В документе одна страница.") }
        let digits = String(count).count
        return [try await OutputPaths.produceFolder(dir, item.baseName) { folder in
            for i in 0..<count {
                try Task.checkCancellation()
                guard let page = document.page(at: i)?.copy() as? PDFPage else { continue }
                let single = PDFDocument()
                single.insert(page, at: 0)
                let number = String(repeating: "0", count: digits - String(i + 1).count) + String(i + 1)
                guard single.write(to: folder.appendingPathComponent("\(item.baseName) \(number).pdf")) else {
                    throw OrbitError("Не удалось сохранить страницу \(i + 1).")
                }
                progress(Double(i + 1) / Double(count))
            }
        }]
    }

    static func toText(_ src: URL, to dst: URL) throws {
        let text = try open(src).string ?? ""
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw OrbitError("В PDF нет текстового слоя. Сохраните страницы картинками и распознайте текст.")
        }
        try text.write(to: dst, atomically: false, encoding: .utf8)
    }
}
