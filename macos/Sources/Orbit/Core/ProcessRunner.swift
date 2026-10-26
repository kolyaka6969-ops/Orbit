import Foundation

/// Запуск системных утилит (ditto, bsdtar, textutil, LibreOffice) с отменой.
enum ProcessRunner {
    @discardableResult
    static func run(_ executable: String, _ arguments: [String], cwd: URL? = nil, stdout: URL? = nil) async throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        if let cwd { process.currentDirectoryURL = cwd }
        process.standardInput = FileHandle.nullDevice

        let errPipe = Pipe()
        process.standardError = errPipe
        let outPipe = Pipe()
        var outFile: FileHandle?
        if let stdout {
            FileManager.default.createFile(atPath: stdout.path, contents: nil)
            outFile = try FileHandle(forWritingTo: stdout)
            process.standardOutput = outFile
        } else {
            process.standardOutput = outPipe
        }

        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<String, Error>) in
                process.terminationHandler = { p in
                    try? outFile?.close()
                    let err = String(data: errPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)?
                        .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                    let out = stdout == nil
                        ? String(data: outPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
                        : ""
                    if p.terminationReason == .uncaughtSignal {
                        cont.resume(throwing: CancellationError())
                    } else if p.terminationStatus == 0 {
                        cont.resume(returning: out)
                    } else {
                        cont.resume(throwing: OrbitError(err.isEmpty ? "Программа завершилась с кодом \(p.terminationStatus)." : err))
                    }
                }
                do {
                    try process.run()
                } catch {
                    try? outFile?.close()
                    cont.resume(throwing: error)
                }
            }
        } onCancel: {
            if process.isRunning { process.terminate() }
        }
    }
}
