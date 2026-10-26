import AppKit
import CoreImage
import ImageIO
import PDFKit

/// Изображения и RAW через ImageIO и Core Graphics — всё встроено в macOS.
enum ImageBackend {
    struct Target {
        let ext: String
        let subtitle: String?
        let uti: String
    }

    private static let candidates: [Target] = [
        Target(ext: "png", subtitle: nil, uti: "public.png"),
        Target(ext: "jpg", subtitle: nil, uti: "public.jpeg"),
        Target(ext: "heic", subtitle: nil, uti: "public.heic"),
        Target(ext: "webp", subtitle: nil, uti: "org.webmproject.webp"),
        Target(ext: "avif", subtitle: nil, uti: "public.avif"),
        Target(ext: "tiff", subtitle: nil, uti: "public.tiff"),
        Target(ext: "gif", subtitle: nil, uti: "com.compuserve.gif"),
        Target(ext: "bmp", subtitle: nil, uti: "com.microsoft.bmp"),
        Target(ext: "ico", subtitle: "значок", uti: "com.microsoft.ico"),
        Target(ext: "pdf", subtitle: "документ", uti: "com.adobe.pdf"),
    ]

    /// Во что умеем конвертировать: только форматы, для которых в этой версии macOS есть кодировщик.
    static let targets: [Target] = {
        let writable = Set((CGImageDestinationCopyTypeIdentifiers() as? [String]) ?? [])
        return candidates.filter { $0.ext == "pdf" || writable.contains($0.uti) }
    }()

    static func canWrite(_ ext: String) -> Bool { targets.contains { $0.ext == ext } }

    private static let lossy: Set<String> = ["jpg", "heic", "webp", "avif"]

    // MARK: чтение и запись

    struct Loaded {
        let image: CGImage
        /// Метаданные для записи (EXIF, GPS, IPTC…), ориентация уже применена к пикселям.
        let metadata: [CFString: Any]
        let hasAlpha: Bool
    }

    static func load(_ url: URL) throws -> Loaded {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil), CGImageSourceGetCount(source) > 0 else {
            throw OrbitError("Не удалось открыть изображение.")
        }
        let props = (CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any]) ?? [:]
        let width = props[kCGImagePropertyPixelWidth] as? Int ?? 0
        let height = props[kCGImagePropertyPixelHeight] as? Int ?? 0
        var image: CGImage?
        if width > 0 && height > 0 {
            // «Миниатюра» полного размера с учётом EXIF-ориентации — так ImageIO поворачивает пиксели за нас.
            let options: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceShouldCacheImmediately: true,
                kCGImageSourceThumbnailMaxPixelSize: max(width, height),
            ]
            image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
        }
        if image == nil { image = CGImageSourceCreateImageAtIndex(source, 0, nil) }
        guard let image else { throw OrbitError("Не удалось прочитать изображение.") }

        var metadata: [CFString: Any] = [kCGImagePropertyOrientation: 1]
        for key in [kCGImagePropertyExifDictionary, kCGImagePropertyGPSDictionary, kCGImagePropertyIPTCDictionary,
                    kCGImagePropertyDPIWidth, kCGImagePropertyDPIHeight] {
            if let value = props[key] { metadata[key] = value }
        }
        if var tiff = props[kCGImagePropertyTIFFDictionary] as? [CFString: Any] {
            tiff[kCGImagePropertyTIFFOrientation] = 1
            metadata[kCGImagePropertyTIFFDictionary] = tiff
        }
        let alpha = (props[kCGImagePropertyHasAlpha] as? Bool) ?? hasAlpha(image)
        return Loaded(image: image, metadata: metadata, hasAlpha: alpha)
    }

    private static func hasAlpha(_ image: CGImage) -> Bool {
        switch image.alphaInfo {
        case .none, .noneSkipFirst, .noneSkipLast: return false
        default: return true
        }
    }

    static func write(_ image: CGImage, to url: URL, ext: String, quality: Double, metadata: [CFString: Any]?) throws {
        if ext == "pdf" {
            try writePdf([image], to: url)
            return
        }
        guard let target = targets.first(where: { $0.ext == ext }),
              let dest = CGImageDestinationCreateWithURL(url as CFURL, target.uti as CFString, 1, nil) else {
            throw OrbitError("Формат \(ext.uppercased()) не поддерживается в этой версии macOS.")
        }
        var img = image
        if ext == "jpg" || ext == "bmp" { img = try flatten(img) }
        if ext == "ico" { img = try fit(img, max: 256) }
        var props = metadata ?? [:]
        if lossy.contains(ext) { props[kCGImageDestinationLossyCompressionQuality] = quality }
        CGImageDestinationAddImage(dest, img, props as CFDictionary)
        guard CGImageDestinationFinalize(dest) else { throw OrbitError("Не удалось сохранить \(ext.uppercased()).") }
    }

    static func writePdf(_ images: [CGImage], to url: URL, progress: ((Double) -> Void)? = nil) throws {
        let document = PDFDocument()
        for (i, image) in images.enumerated() {
            let nsImage = NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
            guard let page = PDFPage(image: nsImage) else { throw OrbitError("Не удалось добавить страницу в PDF.") }
            document.insert(page, at: document.pageCount)
            progress?(Double(i + 1) / Double(images.count))
        }
        guard document.write(to: url) else { throw OrbitError("Не удалось сохранить PDF.") }
    }

    // MARK: рисование

    static func render(width: Int, height: Int, like source: CGImage? = nil, opaque: Bool = false,
                       _ draw: (CGContext) -> Void) throws -> CGImage {
        let sRGB = CGColorSpace(name: CGColorSpace.sRGB)!
        var space = sRGB
        if let s = source?.colorSpace, s.model == .rgb { space = s }
        let info = opaque ? CGImageAlphaInfo.noneSkipLast.rawValue : CGImageAlphaInfo.premultipliedLast.rawValue
        let ctx = CGContext(data: nil, width: max(width, 1), height: max(height, 1), bitsPerComponent: 8, bytesPerRow: 0, space: space, bitmapInfo: info)
            ?? CGContext(data: nil, width: max(width, 1), height: max(height, 1), bitsPerComponent: 8, bytesPerRow: 0, space: sRGB, bitmapInfo: info)
        guard let ctx else { throw OrbitError("Не хватает памяти для изображения.") }
        ctx.interpolationQuality = .high
        if opaque {
            ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
            ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        }
        draw(ctx)
        guard let out = ctx.makeImage() else { throw OrbitError("Не удалось обработать изображение.") }
        return out
    }

    private static func flatten(_ image: CGImage) throws -> CGImage {
        guard hasAlpha(image) else { return image }
        return try render(width: image.width, height: image.height, like: image, opaque: true) { ctx in
            ctx.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        }
    }

    static func scale(_ image: CGImage, by factor: Double) throws -> CGImage {
        let w = max(1, Int((Double(image.width) * factor).rounded()))
        let h = max(1, Int((Double(image.height) * factor).rounded()))
        return try render(width: w, height: h, like: image) { ctx in
            ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        }
    }

    static func fit(_ image: CGImage, max side: Int) throws -> CGImage {
        let longest = Swift.max(image.width, image.height)
        return longest <= side ? image : try scale(image, by: Double(side) / Double(longest))
    }

    /// Поворот на 90°: по часовой стрелке (`clockwise`) или против.
    static func rotate(_ image: CGImage, clockwise: Bool) throws -> CGImage {
        let w = image.width
        let h = image.height
        return try render(width: h, height: w, like: image) { ctx in
            if clockwise {
                ctx.translateBy(x: 0, y: CGFloat(w))
                ctx.rotate(by: -.pi / 2)
            } else {
                ctx.translateBy(x: CGFloat(h), y: 0)
                ctx.rotate(by: .pi / 2)
            }
            ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        }
    }

    static func mirror(_ image: CGImage) throws -> CGImage {
        try render(width: image.width, height: image.height, like: image) { ctx in
            ctx.translateBy(x: CGFloat(image.width), y: 0)
            ctx.scaleBy(x: -1, y: 1)
            ctx.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        }
    }

    static func grayscale(_ image: CGImage) throws -> CGImage {
        let input = CIImage(cgImage: image)
        guard let filter = CIFilter(name: "CIColorControls") else { throw OrbitError("Фильтр недоступен.") }
        filter.setValue(input, forKey: kCIInputImageKey)
        filter.setValue(0.0, forKey: kCIInputSaturationKey)
        guard let output = filter.outputImage,
              let result = CIContext().createCGImage(output, from: input.extent) else {
            throw OrbitError("Не удалось обработать изображение.")
        }
        return result
    }

    static func cropSquare(_ image: CGImage) throws -> CGImage {
        let side = min(image.width, image.height)
        let rect = CGRect(x: (image.width - side) / 2, y: (image.height - side) / 2, width: side, height: side)
        guard let cropped = image.cropping(to: rect) else { throw OrbitError("Не удалось обрезать изображение.") }
        return cropped
    }

    // MARK: операции

    /// В каком формате сохранять результат правки: как исходный, если это возможно.
    static func editFormat(for item: FileItem) -> String {
        if item.kind == .raw { return "jpg" }
        let ext = Formats.canonical(item.ext)
        if ext != "pdf" && ext != "ico" && canWrite(ext) { return ext }
        return "png"
    }

    /// «Сжать»: сжимаемые с потерями форматы пережимаются, остальные становятся JPG (или HEIC, если есть прозрачность).
    static func compressFormat(for item: FileItem) -> String {
        let ext = item.kind == .raw ? "jpg" : Formats.canonical(item.ext)
        if lossy.contains(ext) && canWrite(ext) { return ext }
        let alpha = (try? load(item.url).hasAlpha) ?? false
        if alpha { return canWrite("heic") ? "heic" : "png" }
        return "jpg"
    }

    static func convert(_ src: URL, to dst: URL, ext: String, quality: Double) throws {
        let loaded = try load(src)
        try write(loaded.image, to: dst, ext: ext, quality: quality, metadata: loaded.metadata)
    }

    static func compress(_ src: URL, to dst: URL, ext: String) throws {
        let loaded = try load(src)
        try write(loaded.image, to: dst, ext: ext, quality: 0.6, metadata: nil)
    }

    /// Правка с сохранением метаданных (`keepMetadata: false` — без EXIF и GPS).
    static func edit(_ src: URL, to dst: URL, ext: String, quality: Double, keepMetadata: Bool = true,
                     _ transform: (CGImage) throws -> CGImage) throws {
        let loaded = try load(src)
        let result = try transform(loaded.image)
        try write(result, to: dst, ext: ext, quality: quality, metadata: keepMetadata ? loaded.metadata : nil)
    }

    static func imagesToPdf(_ urls: [URL], to dst: URL, progress: @escaping (Double) -> Void) throws {
        let document = PDFDocument()
        for (i, url) in urls.enumerated() {
            try Task.checkCancellation()
            let image = try load(url).image
            let nsImage = NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
            guard let page = PDFPage(image: nsImage) else { throw OrbitError("Не удалось добавить \(url.lastPathComponent) в PDF.") }
            document.insert(page, at: document.pageCount)
            progress(Double(i + 1) / Double(urls.count))
        }
        guard document.write(to: dst) else { throw OrbitError("Не удалось сохранить PDF.") }
    }
}
