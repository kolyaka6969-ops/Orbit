import AppKit

/// Одна запущенная операция — строка в панели задач.
@MainActor
final class Job: ObservableObject, Identifiable {
    enum State {
        case running
        case done
        case failed
        case canceled
        /// Просто сообщение, без операции.
        case info
    }

    let id = UUID()
    let title: String
    let symbol: String
    let instant: Bool
    @Published var progress: Double = 0
    @Published var state: State
    @Published var detail: String
    @Published var outputs: [URL] = []
    var task: Task<Void, Never>?

    init(_ op: Operation, _ items: [FileItem]) {
        let label = op.wheel == .convert && !(op.subtitle ?? "").isEmpty ? "\(op.label) · \(op.subtitle!)" : op.label
        title = "\(label) — \(filesCaption(items))"
        symbol = op.symbol
        instant = op.instant
        state = .running
        detail = "Выполняется…"
    }

    init(info title: String, _ detail: String) {
        self.title = title
        self.detail = detail
        symbol = Symbols.info
        instant = true
        state = .info
    }

    var isRunning: Bool { state == .running }

    func cancel() { task?.cancel() }

    func reveal() {
        let existing = outputs.filter { FileManager.default.fileExists(atPath: $0.path) }
        if !existing.isEmpty { NSWorkspace.shared.activateFileViewerSelecting(existing) }
    }
}

@MainActor
final class JobRunner: ObservableObject {
    @Published private(set) var jobs: [Job] = []
    /// Появилась задача или сообщение — показать панель.
    var onActivity: (() -> Void)?
    /// Все задачи завершились.
    var onIdle: (() -> Void)?

    private let settings: Settings

    init(settings: Settings) {
        self.settings = settings
    }

    var hasRunning: Bool { jobs.contains { $0.isRunning } }

    func start(_ op: Operation, _ items: [FileItem]) {
        let job = Job(op, items)
        add(job)
        let context = JobContext(settings: SettingsSnapshot.current()) { @Sendable [weak job] value in
            Task { @MainActor in job?.progress = value }
        }
        job.task = Task { @MainActor in
            do {
                let outputs = try await op.run(items, context)
                job.outputs = outputs
                job.state = .done
                job.detail = context.message ?? Self.doneText(outputs)
            } catch {
                if Task.isCancelled || error is CancellationError {
                    job.state = .canceled
                    job.detail = "Отменено"
                } else {
                    job.state = .failed
                    job.detail = error.localizedDescription
                }
            }
            job.progress = 1
            if !self.hasRunning { self.onIdle?() }
        }
    }

    func info(_ title: String, _ detail: String) {
        add(Job(info: title, detail))
        onIdle?()
    }

    func clearFinished() {
        jobs.removeAll { !$0.isRunning }
    }

    private func add(_ job: Job) {
        jobs.insert(job, at: 0)
        if jobs.count > 20 { jobs.removeLast(jobs.count - 20) }
        if settings.showJobs || !job.instant { onActivity?() }
    }

    private static func doneText(_ outputs: [URL]) -> String {
        switch outputs.count {
        case 0: return "Готово"
        case 1: return outputs[0].lastPathComponent
        default: return "Готово: \(outputs.count) \(plural(outputs.count, "файл", "файла", "файлов"))"
        }
    }
}
