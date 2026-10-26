import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let settings: Settings
    private let input = GlobalInput()
    private let hotkey = HotKey()
    private let permissions: Permissions
    private lazy var jobs = JobRunner(settings: settings)
    private var controller: WheelController!
    private var jobsPanel: JobsPanel!
    private var status: StatusMenu!
    private var settingsWindow: SettingsWindow!
    private let services = ServiceProvider()
    private var pendingURLs: [URL] = []
    private var inputTask: Task<Void, Never>?

    override init() {
        settings = Settings.shared
        permissions = Permissions()
        super.init()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        settings.applyTheme()
        controller = WheelController(settings: settings, jobs: jobs, input: input)
        jobsPanel = JobsPanel(runner: jobs)
        jobs.onActivity = { [weak self] in self?.jobsPanel.show() }
        jobs.onIdle = { [weak self] in self?.jobsPanel.scheduleHide() }
        controller.onNotify = { [weak self] title, text in self?.jobs.info(title, text) }
        settingsWindow = SettingsWindow(settings: settings, permissions: permissions)
        status = StatusMenu(settings: settings,
                            onClipboard: { [weak self] in self?.controller.openForClipboard() },
                            onJobs: { [weak self] in self?.jobsPanel.show() },
                            onSettings: { [weak self] in self?.settingsWindow.show() })

        hotkey.onPress = { [weak self] in self?.controller.onHotkey() }
        applyHotkey()
        settings.onChange = { [weak self] in
            self?.applyHotkey()
            self?.status.refresh()
        }

        services.onFiles = { [weak self] urls in self?.controller.queue(urls) }
        NSApp.servicesProvider = services
        NSUpdateDynamicServices()

        permissions.isGestureRunning = { [weak self] in self?.input.isRunning ?? false }
        startInput()

        if !settings.welcomed || !GlobalInput.hasPermission {
            settings.welcomed = true
            settingsWindow.show()
        }
        if !pendingURLs.isEmpty {
            controller.queue(pendingURLs)
            pendingURLs.removeAll()
        }
    }

    /// Файлы, брошенные на значок Orbit, или «Открыть в программе → Orbit».
    func application(_ application: NSApplication, open urls: [URL]) {
        if let controller {
            controller.queue(urls)
        } else {
            pendingURLs += urls
        }
    }

    /// Повторный запуск Orbit (например, из Launchpad) открывает настройки.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        settingsWindow.show()
        return false
    }

    private func applyHotkey() {
        if settings.hotkeyEnabled { hotkey.register() } else { hotkey.unregister() }
    }

    /// Перехват жеста требует «Мониторинга ввода»: пока разрешения нет, пробуем снова раз в пару секунд.
    private func startInput() {
        if input.start() {
            permissions.refresh()
            return
        }
        GlobalInput.requestPermission()
        inputTask = Task { @MainActor in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 2_000_000_000)
                if self.input.start() {
                    self.permissions.refresh()
                    return
                }
                self.permissions.refresh()
            }
        }
    }
}

/// Служба «Открыть в Orbit» (Finder → правый щелчок → Службы).
final class ServiceProvider: NSObject {
    var onFiles: (@MainActor ([URL]) -> Void)?

    @objc func openFiles(_ pboard: NSPasteboard, userData: String?, error: AutoreleasingUnsafeMutablePointer<NSString?>) {
        let objects = pboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) ?? []
        let urls = objects.compactMap { ($0 as? NSURL) as URL? }
        let callback = onFiles
        Task { @MainActor in callback?(urls) }
    }
}
