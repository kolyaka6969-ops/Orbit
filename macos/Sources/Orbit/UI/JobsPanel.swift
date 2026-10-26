import AppKit
import SwiftUI

/// Панель задач в правом верхнем углу экрана: прогресс, ошибки, кнопка «Показать в Finder».
@MainActor
final class JobsPanel {
    private static let width: CGFloat = 360
    private static let rowHeight: CGFloat = 58
    private static let headerHeight: CGFloat = 40
    private static let maxRows = 4

    private let runner: JobRunner
    private let panel: NSPanel
    private var hideTask: Task<Void, Never>?

    init(runner: JobRunner) {
        self.runner = runner
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: Self.width, height: 100),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false

        let effect = NSVisualEffectView()
        effect.material = .popover
        effect.blendingMode = .behindWindow
        effect.state = .active
        effect.wantsLayer = true
        effect.layer?.cornerRadius = 12
        effect.layer?.masksToBounds = true
        let host = NSHostingView(rootView: JobsView(runner: runner, onClose: { [weak self] in self?.hide() }))
        host.translatesAutoresizingMaskIntoConstraints = false
        effect.addSubview(host)
        NSLayoutConstraint.activate([
            host.leadingAnchor.constraint(equalTo: effect.leadingAnchor),
            host.trailingAnchor.constraint(equalTo: effect.trailingAnchor),
            host.topAnchor.constraint(equalTo: effect.topAnchor),
            host.bottomAnchor.constraint(equalTo: effect.bottomAnchor),
        ])
        panel.contentView = effect
    }

    func show() {
        hideTask?.cancel()
        layout()
        panel.orderFrontRegardless()
        if !runner.hasRunning { scheduleHide() }
    }

    func hide() {
        hideTask?.cancel()
        panel.orderOut(nil)
    }

    /// Все задачи завершились: панель уходит через несколько секунд, если её не трогают.
    func scheduleHide() {
        hideTask?.cancel()
        guard panel.isVisible else { return }
        layout()
        hideTask = Task { @MainActor in
            try? await Task.sleep(nanoseconds: 5_000_000_000)
            guard !Task.isCancelled else { return }
            let mouseInside = NSMouseInRect(NSEvent.mouseLocation, self.panel.frame, false)
            if self.runner.hasRunning || mouseInside {
                self.scheduleHide()
            } else {
                self.panel.orderOut(nil)
            }
        }
    }

    private func layout() {
        let rows = CGFloat(min(max(runner.jobs.count, 1), Self.maxRows))
        let height = Self.headerHeight + rows * Self.rowHeight + 8
        let screen = NSScreen.main ?? NSScreen.screens.first
        let visible = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        panel.setFrame(NSRect(x: visible.maxX - Self.width - 12, y: visible.maxY - height - 12, width: Self.width, height: height),
                       display: true)
    }
}

private struct JobsView: View {
    @ObservedObject var runner: JobRunner
    let onClose: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("Orbit").font(.headline)
                Spacer()
                if runner.jobs.contains(where: { !$0.isRunning }) {
                    Button("Очистить") { runner.clearFinished() }
                        .buttonStyle(.borderless)
                        .font(.caption)
                }
                Button(action: onClose) { Image(systemName: "xmark") }
                    .buttonStyle(.borderless)
                    .help("Скрыть")
            }
            .padding(.horizontal, 14)
            .frame(height: 40)
            if runner.jobs.isEmpty {
                Text("Задач нет").foregroundStyle(.secondary).padding(.horizontal, 14).frame(height: 58)
            }
            ForEach(Array(runner.jobs.prefix(4))) { job in
                JobRow(job: job)
            }
            Spacer(minLength: 0)
        }
        .frame(width: 360)
    }
}

private struct JobRow: View {
    @ObservedObject var job: Job

    private var icon: (name: String, color: Color) {
        switch job.state {
        case .running: return (job.symbol, .accentColor)
        case .done: return ("checkmark.circle.fill", .green)
        case .failed: return ("exclamationmark.triangle.fill", .orange)
        case .canceled: return ("xmark.circle", .secondary)
        case .info: return (Symbols.info, .accentColor)
        }
    }

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: icon.name)
                .foregroundStyle(icon.color)
                .font(.system(size: 16))
                .frame(width: 22)
            VStack(alignment: .leading, spacing: 4) {
                Text(job.title).lineLimit(1).truncationMode(.middle)
                if job.isRunning && !job.instant {
                    if job.progress > 0 {
                        ProgressView(value: job.progress).progressViewStyle(.linear)
                    } else {
                        ProgressView().progressViewStyle(.linear)
                    }
                } else {
                    Text(job.detail).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                }
            }
            Spacer(minLength: 4)
            if job.isRunning {
                Button(action: job.cancel) { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.borderless)
                    .help("Отменить")
            } else if job.state == .done && !job.outputs.isEmpty {
                Button("Показать") { job.reveal() }
                    .buttonStyle(.borderless)
            }
        }
        .padding(.horizontal, 14)
        .frame(height: 58)
    }
}
