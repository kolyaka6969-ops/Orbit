import AppKit

/// Значок Orbit в строке меню.
@MainActor
final class StatusMenu: NSObject, NSMenuDelegate {
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    private let settings: Settings
    private let onClipboard: () -> Void
    private let onJobs: () -> Void
    private let onSettings: () -> Void

    init(settings: Settings, onClipboard: @escaping () -> Void, onJobs: @escaping () -> Void, onSettings: @escaping () -> Void) {
        self.settings = settings
        self.onClipboard = onClipboard
        self.onJobs = onJobs
        self.onSettings = onSettings
        super.init()
        let menu = NSMenu()
        menu.delegate = self
        item.menu = menu
        refresh()
    }

    func refresh() {
        let image = NSImage(systemSymbolName: settings.paused ? "pause.circle" : "circle.circle", accessibilityDescription: "Orbit")
        image?.isTemplate = true
        item.button?.image = image
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        menu.addItem(entry("Круг для буфера обмена", #selector(clipboard)))
        menu.addItem(entry("Панель задач", #selector(jobs)))
        menu.addItem(.separator())
        let pause = entry("Приостановить", #selector(togglePause))
        pause.state = settings.paused ? .on : .off
        menu.addItem(pause)
        menu.addItem(entry("Настройки…", #selector(openSettings), key: ","))
        menu.addItem(.separator())
        menu.addItem(entry("Выйти из Orbit", #selector(quit), key: "q"))
    }

    private func entry(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let menuItem = NSMenuItem(title: title, action: action, keyEquivalent: key)
        menuItem.target = self
        return menuItem
    }

    @objc private func clipboard() { onClipboard() }

    @objc private func jobs() { onJobs() }

    @objc private func togglePause() {
        settings.paused.toggle()
        refresh()
    }

    @objc private func openSettings() { onSettings() }

    @objc private func quit() { NSApp.terminate(nil) }
}
