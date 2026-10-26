import CryptoKit
import Foundation

/// Архивы системными утилитами: ditto для ZIP, bsdtar (libarchive) для 7z, RAR, TAR и сжатых TAR.
enum ArchiveBackend {
    private static let tarSuffixes = [".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz"]

    static func extract(_ item: FileItem, dir: URL) async throws -> URL {
        let lower = item.name.lowercased()
        let isTar = tarSuffixes.contains { lower.hasSuffix($0) }
        if !isTar && (item.ext == "gz" || item.ext == "bz2") {
            // Один сжатый файл, не архив: «отчёт.txt.gz» → «отчёт.txt».
            let inner = item.url.deletingPathExtension()
            let tool = item.ext == "gz" ? "/usr/bin/gzip" : "/usr/bin/bzip2"
            return try await OutputPaths.produce(dir, inner.deletingPathExtension().lastPathComponent, inner.pathExtension) { tmp in
                try await ProcessRunner.run(tool, ["-dc", item.path], stdout: tmp)
            }
        }
        return try await OutputPaths.produceFolder(dir, item.baseName) { folder in
            if item.ext == "zip" {
                try await ProcessRunner.run("/usr/bin/ditto", ["-x", "-k", item.path, folder.path])
            } else {
                try await ProcessRunner.run("/usr/bin/bsdtar", ["-xf", item.path, "-C", folder.path])
            }
        }
    }

    static func zip(_ items: [FileItem], to dst: URL) async throws {
        if items.count == 1 {
            try await ProcessRunner.run("/usr/bin/ditto", ["-c", "-k", "--sequesterRsrc", "--keepParent", items[0].path, dst.path])
            return
        }
        let folder = items[0].folder
        let sameFolder = items.allSatisfy { $0.folder.path == folder.path }
        let names = sameFolder ? items.map { "./" + $0.name } : items.map(\.path)
        try await ProcessRunner.run("/usr/bin/zip", ["-r", "-q", "-y", dst.path] + names + ["-x", "*.DS_Store"], cwd: folder)
    }

    static func sha256(_ url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while true {
            try Task.checkCancellation()
            let chunk = try handle.read(upToCount: 1 << 20) ?? Data()
            if chunk.isEmpty { break }
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }
}
