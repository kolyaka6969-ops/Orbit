import AppKit
import ServiceManagement

enum OutputMode: String, CaseIterable {
    /// Рядом с исходным файлом.
    case sameFolder
    /// В подпапку «Orbit» рядом с исходным файлом.
    case subfolder
    /// В выбранную пользователем папку.
    case custom
}

enum ThemeChoice: String, CaseIterable {
    case system
    case light
    case dark
}

enum VideoQuality: String, CaseIterable {
    case small
    case balanced
    case high
}

/// Чем конвертировать документы Word и таблицы Excel.
enum OfficeEngine: String, CaseIterable {
    /// LibreOffice, если установлен, иначе встроенный конвертер macOS.
    case auto
    /// textutil и печать в PDF — только документы.
    case builtIn
    case libreOffice
}

private enum Key {
    static let theme = "theme"
    static let shiftWheel = "shiftWheel"
    static let controlWheel = "controlWheel"
    static let optionWheel = "optionWheel"
    static let hotkey = "hotkeyEnabled"
    static let paused = "paused"
    static let showJobs = "showJobs"
    static let output = "output"
    static let customFolder = "customFolder"
    static let imageQuality = "imageQuality"
    static let video = "video"
    static let pdfDpi = "pdfDpi"
    static let office = "office"
    static let welcomed = "welcomed"
}

/// Значения, которые нужны операциям. Читаются прямо из UserDefaults, поэтому доступны из любого потока.
struct SettingsSnapshot {
    var output: OutputMode
    var customFolder: String
    var imageQuality: Int
    var video: VideoQuality
    var pdfDpi: Int
    var office: OfficeEngine

    static func current() -> SettingsSnapshot {
        let d = UserDefaults.standard
        return SettingsSnapshot(
            output: OutputMode(rawValue: d.string(forKey: Key.output) ?? "") ?? .sameFolder,
            customFolder: d.string(forKey: Key.customFolder) ?? "",
            imageQuality: d.object(forKey: Key.imageQuality) as? Int ?? 85,
            video: VideoQuality(rawValue: d.string(forKey: Key.video) ?? "") ?? .balanced,
            pdfDpi: d.object(forKey: Key.pdfDpi) as? Int ?? 150,
            office: OfficeEngine(rawValue: d.string(forKey: Key.office) ?? "") ?? .auto
        )
    }
}

/// Настройки в UserDefaults (~/Library/Preferences/<bundle id>.plist). Автозапуск — через SMAppService.
@MainActor
final class Settings: ObservableObject {
    static let shared = Settings()

    private let d = UserDefaults.standard

    /// Вызывается после любого изменения.
    var onChange: (() -> Void)?

    @Published var theme: ThemeChoice { didSet { store(theme.rawValue, Key.theme); applyTheme() } }
    @Published var shiftWheel: Bool { didSet { store(shiftWheel, Key.shiftWheel) } }
    @Published var controlWheel: Bool { didSet { store(controlWheel, Key.controlWheel) } }
    @Published var optionWheel: Bool { didSet { store(optionWheel, Key.optionWheel) } }
    @Published var hotkeyEnabled: Bool { didSet { store(hotkeyEnabled, Key.hotkey) } }
    @Published var paused: Bool { didSet { store(paused, Key.paused) } }
    @Published var showJobs: Bool { didSet { store(showJobs, Key.showJobs) } }
    @Published var output: OutputMode { didSet { store(output.rawValue, Key.output) } }
    @Published var customFolder: String { didSet { store(customFolder, Key.customFolder) } }
    @Published var imageQuality: Int { didSet { store(min(max(imageQuality, 10), 100), Key.imageQuality) } }
    @Published var video: VideoQuality { didSet { store(video.rawValue, Key.video) } }
    @Published var pdfDpi: Int { didSet { store(pdfDpi, Key.pdfDpi) } }
    @Published var office: OfficeEngine { didSet { store(office.rawValue, Key.office) } }
    @Published var welcomed: Bool { didSet { store(welcomed, Key.welcomed) } }

    private init() {
        let d = UserDefaults.standard
        let s = SettingsSnapshot.current()
        theme = ThemeChoice(rawValue: d.string(forKey: Key.theme) ?? "") ?? .system
        shiftWheel = d.object(forKey: Key.shiftWheel) as? Bool ?? true
        controlWheel = d.object(forKey: Key.controlWheel) as? Bool ?? true
        optionWheel = d.object(forKey: Key.optionWheel) as? Bool ?? true
        hotkeyEnabled = d.object(forKey: Key.hotkey) as? Bool ?? true
        paused = d.bool(forKey: Key.paused)
        showJobs = d.object(forKey: Key.showJobs) as? Bool ?? true
        output = s.output
        customFolder = s.customFolder
        imageQuality = s.imageQuality
        video = s.video
        pdfDpi = s.pdfDpi
        office = s.office
        welcomed = d.bool(forKey: Key.welcomed)
    }

    private func store(_ value: Any, _ key: String) {
        d.set(value, forKey: key)
        onChange?()
    }

    func wheel(for key: ModifierKey) -> WheelKind? {
        switch key {
        case .shift: return shiftWheel ? .convert : nil
        case .control: return controlWheel ? .tools : nil
        case .option: return optionWheel ? .actions : nil
        }
    }

    func applyTheme() {
        switch theme {
        case .system: NSApp.appearance = nil
        case .light: NSApp.appearance = NSAppearance(named: .aqua)
        case .dark: NSApp.appearance = NSAppearance(named: .darkAqua)
        }
    }

    // MARK: автозапуск

    var launchAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            do {
                if newValue {
                    try SMAppService.mainApp.register()
                } else {
                    try SMAppService.mainApp.unregister()
                }
            } catch {
                NSLog("Orbit: автозапуск: \(error.localizedDescription)")
            }
            objectWillChange.send()
        }
    }
}
