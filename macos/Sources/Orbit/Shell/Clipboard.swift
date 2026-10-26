import AppKit

@MainActor
enum Clipboard {
    static func setText(_ text: String) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
    }

    static func setFiles(_ urls: [URL]) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.writeObjects(urls.map { $0 as NSURL })
    }

    static func fileURLs(_ pb: NSPasteboard) -> [URL] {
        let objects = pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) ?? []
        return objects.compactMap { ($0 as? NSURL) as URL? }
    }

    /// Файлы из буфера обмена, а если там картинка (например, снимок ⌘⇧⌃4) — она, сохранённая в PNG.
    static func files() -> [URL] {
        let pb = NSPasteboard.general
        let urls = fileURLs(pb)
        if !urls.isEmpty { return urls }
        guard let image = NSImage(pasteboard: pb),
              let tiff = image.tiffRepresentation,
              let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) else { return [] }
        let pictures = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        let folder = pictures.appendingPathComponent("Orbit", isDirectory: true)
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd 'в' HH.mm.ss"
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let url = OutputPaths.unique(folder, "Из буфера обмена \(formatter.string(from: Date()))", "png")
            try png.write(to: url)
            return [url]
        } catch {
            return []
        }
    }
}
