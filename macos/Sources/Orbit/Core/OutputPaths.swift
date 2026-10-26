import Foundation

/// Имена результатов. Исходные файлы никогда не перезаписываются: если имя занято, добавляется « (2)», « (3)»…
/// Результат сначала пишется во временный файл, поэтому прерванная конвертация не оставляет половину файла.
enum OutputPaths {
    private static let lock = NSLock()

    static func unique(_ dir: URL, _ baseName: String, _ ext: String) -> URL {
        let suffix = ext.isEmpty ? "" : "." + ext
        var candidate = dir.appendingPathComponent(baseName + suffix)
        var i = 2
        while FileManager.default.fileExists(atPath: candidate.path) {
            candidate = dir.appendingPathComponent("\(baseName) (\(i))\(suffix)")
            i += 1
        }
        return candidate
    }

    /// Файл-результат: `write` получает временный путь в той же папке.
    static func produce(_ dir: URL, _ baseName: String, _ ext: String, _ write: (URL) async throws -> Void) async throws -> URL {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let temp = dir.appendingPathComponent(".orbit-\(UUID().uuidString)" + (ext.isEmpty ? "" : "." + ext))
        defer { try? FileManager.default.removeItem(at: temp) }
        try await write(temp)
        guard FileManager.default.fileExists(atPath: temp.path) else { throw OrbitError("Результат не был создан.") }
        return try commit(temp, dir, baseName, ext)
    }

    /// Папка-результат (страницы PDF, распакованный архив): `fill` получает пустую временную папку.
    static func produceFolder(_ dir: URL, _ name: String, _ fill: (URL) async throws -> Void) async throws -> URL {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let temp = dir.appendingPathComponent(".orbit-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: temp) }
        try await fill(temp)
        return try commit(temp, dir, name, "")
    }

    private static func commit(_ temp: URL, _ dir: URL, _ baseName: String, _ ext: String) throws -> URL {
        lock.lock()
        defer { lock.unlock() }
        let final = unique(dir, baseName, ext)
        try FileManager.default.moveItem(at: temp, to: final)
        return final
    }
}
