import AppKit

/// Решает, когда открыть круг (жест при перетаскивании, горячая клавиша, служба Finder) и с какими пунктами.
@MainActor
final class WheelController {
    private let settings: Settings
    private let jobs: JobRunner
    private let input: GlobalInput
    let operations = Catalog.build()
    private var wheel: WheelView?
    private var pendingURLs: [URL] = []
    private var pendingTask: Task<Void, Never>?

    /// Сообщение без операции — через ту же панель задач.
    var onNotify: ((String, String) -> Void)?

    init(settings: Settings, jobs: JobRunner, input: GlobalInput) {
        self.settings = settings
        self.jobs = jobs
        self.input = input

        input.onModifierTap = { [weak self] key in self?.onModifierTap(key) }
        input.onLeftMouseUp = { [weak self] in self?.wheel?.onDragEnded() }
        input.onMouseDown = { [weak self] point in
            guard let wheel = self?.wheel, !wheel.isFinished, wheel.mode == .click else { return }
            if !wheel.contains(point) { wheel.finish() }
        }
        input.onEscape = { [weak self] in
            guard let wheel = self?.wheel, !wheel.isFinished, wheel.mode == .drag else { return }
            wheel.finish()
        }
    }

    private func operationsFor(_ kind: WheelKind, _ items: [FileItem]) -> [Operation] {
        operations.filter { $0.wheel == kind && $0.appliesTo(items) }
    }

    private func onModifierTap(_ key: ModifierKey) {
        if let open = wheel, !open.isFinished {
            if !settings.paused, let next = settings.wheel(for: key) { open.switchTo(next) }
            return
        }
        guard !settings.paused, let kind = settings.wheel(for: key), input.isDragging, let files = input.draggedFiles() else {
            input.wheelActive = wheel != nil
            return
        }
        open(.drag, kind, files.map(FileItem.init), at: NSEvent.mouseLocation)
    }

    /// ⌃⌥Пробел: файлы, выделенные в Finder (или скопированные в буфер обмена).
    func onHotkey() {
        guard !settings.paused else { return }
        var urls = Finder.isFrontmost ? Finder.selection() : []
        if urls.isEmpty { urls = Clipboard.files() }
        if urls.isEmpty {
            onNotify?("Ничего не выбрано", "Выделите файлы в Finder или скопируйте их (⌘C) и нажмите ⌃⌥Пробел ещё раз.")
            return
        }
        openFor(urls)
    }

    func openForClipboard() {
        let urls = Clipboard.files()
        if urls.isEmpty {
            onNotify?("Буфер обмена пуст", "Скопируйте файлы или изображение, например снимок экрана (⌘⇧⌃4).")
            return
        }
        openFor(urls)
    }

    /// Пути из службы Finder и «Открыть в программе» (могут прийти несколькими вызовами подряд).
    func queue(_ urls: [URL]) {
        pendingURLs += urls
        pendingTask?.cancel()
        pendingTask = Task { @MainActor in
            try? await Task.sleep(nanoseconds: 250_000_000)
            guard !Task.isCancelled else { return }
            var seen = Set<String>()
            let unique = self.pendingURLs.filter { seen.insert($0.path).inserted }
            self.pendingURLs.removeAll()
            self.openFor(unique)
        }
    }

    func openFor(_ urls: [URL]) {
        let items = urls.filter { FileManager.default.fileExists(atPath: $0.path) }.map(FileItem.init)
        guard !items.isEmpty else { return }
        open(.click, .convert, items, at: NSEvent.mouseLocation)
    }

    private func open(_ mode: WheelMode, _ kind: WheelKind, _ items: [FileItem], at point: NSPoint) {
        wheel?.finish()
        let view = WheelView(mode: mode, wheel: kind, items: items,
                             source: { [weak self] kind, items in self?.operationsFor(kind, items) ?? [] },
                             execute: { [weak self] op, targets in self?.jobs.start(op, targets) })
        view.onClose = { [weak self, weak view] in
            guard let self, let view, self.wheel === view else { return }
            self.wheel = nil
            self.input.wheelActive = false
        }
        wheel = view
        input.wheelActive = true
        view.show(at: point)
    }
}
