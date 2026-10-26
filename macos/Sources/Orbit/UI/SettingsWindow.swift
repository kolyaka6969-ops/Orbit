import AppKit
import SwiftUI

@MainActor
final class SettingsWindow {
    private var window: NSWindow?
    private let settings: Settings
    private let permissions: Permissions

    init(settings: Settings, permissions: Permissions) {
        self.settings = settings
        self.permissions = permissions
    }

    func show() {
        if window == nil {
            let controller = NSHostingController(rootView: SettingsView(settings: settings, permissions: permissions))
            let window = NSWindow(contentViewController: controller)
            window.title = "Orbit"
            window.styleMask = [.titled, .closable, .miniaturizable]
            window.isReleasedWhenClosed = false
            window.setContentSize(NSSize(width: 640, height: 560))
            window.center()
            self.window = window
        }
        permissions.refresh()
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}

/// Состояние разрешения «Мониторинг ввода» для интерфейса.
@MainActor
final class Permissions: ObservableObject {
    @Published private(set) var inputMonitoring = GlobalInput.hasPermission
    @Published private(set) var gestureRunning = false
    var isGestureRunning: () -> Bool = { false }

    func refresh() {
        inputMonitoring = GlobalInput.hasPermission
        gestureRunning = isGestureRunning()
    }

    func request() {
        GlobalInput.requestPermission()
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent") {
            NSWorkspace.shared.open(url)
        }
    }

    /// После выдачи разрешения перехват иногда заработает только в новом процессе.
    func relaunch() {
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: Bundle.main.bundleURL, configuration: configuration) { _, _ in
            Task { @MainActor in NSApp.terminate(nil) }
        }
    }
}

private struct SettingsView: View {
    @ObservedObject var settings: Settings
    @ObservedObject var permissions: Permissions

    var body: some View {
        TabView {
            GeneralTab(settings: settings, permissions: permissions)
                .tabItem { Label("Главная", systemImage: "house") }
            GesturesTab(settings: settings)
                .tabItem { Label("Жесты", systemImage: "hand.draw") }
            OutputTab(settings: settings)
                .tabItem { Label("Сохранение", systemImage: "square.and.arrow.down") }
            FormatsTab()
                .tabItem { Label("Форматы", systemImage: "doc.on.doc") }
            AboutTab()
                .tabItem { Label("О программе", systemImage: "info.circle") }
        }
        .frame(width: 640, height: 560)
    }
}

private struct Keycap: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.system(size: 12, weight: .semibold))
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(RoundedRectangle(cornerRadius: 5).fill(Color.secondary.opacity(0.15)))
            .overlay(RoundedRectangle(cornerRadius: 5).stroke(Color.secondary.opacity(0.3)))
    }
}

private struct GeneralTab: View {
    @ObservedObject var settings: Settings
    @ObservedObject var permissions: Permissions

    var body: some View {
        Form {
            if !permissions.gestureRunning {
                Section {
                    VStack(alignment: .leading, spacing: 8) {
                        Label("Нужно разрешение «Мониторинг ввода»", systemImage: "exclamationmark.triangle.fill")
                            .font(.headline)
                            .foregroundStyle(.orange)
                        Text("Без него Orbit не замечает Shift, Control и Option во время перетаскивания. Включите Orbit в «Системные настройки → Конфиденциальность и безопасность → Мониторинг ввода». Горячая клавиша и служба Finder работают и без него.")
                            .font(.callout)
                            .fixedSize(horizontal: false, vertical: true)
                        HStack {
                            Button("Открыть настройки") { permissions.request() }
                            Button("Перезапустить Orbit") { permissions.relaunch() }
                        }
                    }
                    .padding(.vertical, 4)
                }
            }

            Section("Перетащите файл из Finder и нажмите клавишу") {
                HStack(spacing: 10) { Keycap(text: "⇧ Shift"); Text("конвертация") }
                HStack(spacing: 10) { Keycap(text: "⌃ Control"); Text("инструменты") }
                HStack(spacing: 10) { Keycap(text: "⌥ Option"); Text("действия") }
                HStack(spacing: 6) {
                    Text("Без мыши:").foregroundStyle(.secondary)
                    Keycap(text: "⌃ Control")
                    Text("+")
                    Keycap(text: "⌥ Option")
                    Text("+")
                    Keycap(text: "Пробел")
                    Text("для выделенных в Finder файлов").foregroundStyle(.secondary)
                }
            }

            Section("Запуск") {
                Toggle("Открывать Orbit при входе в систему", isOn: Binding(
                    get: { settings.launchAtLogin },
                    set: { settings.launchAtLogin = $0 }))
                Toggle("Приостановить Orbit", isOn: $settings.paused)
            }

            Section("Оформление") {
                Picker("Тема", selection: $settings.theme) {
                    Text("Как в системе").tag(ThemeChoice.system)
                    Text("Светлая").tag(ThemeChoice.light)
                    Text("Тёмная").tag(ThemeChoice.dark)
                }
                Toggle("Панель задач в углу экрана", isOn: $settings.showJobs)
            }
        }
        .formStyle(.grouped)
    }
}

private struct GesturesTab: View {
    @ObservedObject var settings: Settings

    var body: some View {
        Form {
            Section("Во время перетаскивания файла") {
                Toggle(isOn: $settings.shiftWheel) {
                    VStack(alignment: .leading) {
                        Text("⇧ Shift — конвертация")
                        Text("PNG, JPG, HEIC, M4A, MP4, страницы PDF, распаковка архивов…").font(.caption).foregroundStyle(.secondary)
                    }
                }
                Toggle(isOn: $settings.controlWheel) {
                    VStack(alignment: .leading) {
                        Text("⌃ Control — инструменты")
                        Text("Сжать, уменьшить, повернуть, распознать текст, объединить в PDF, ZIP, SHA-256…").font(.caption).foregroundStyle(.secondary)
                    }
                }
                Toggle(isOn: $settings.optionWheel) {
                    VStack(alignment: .leading) {
                        Text("⌥ Option — действия")
                        Text("Копировать путь, в буфер обмена, на рабочий стол, переместить, в корзину…").font(.caption).foregroundStyle(.secondary)
                    }
                }
            }

            Section("Без перетаскивания") {
                Toggle("Горячая клавиша ⌃⌥Пробел", isOn: $settings.hotkeyEnabled)
                LabeledContent("Служба «Открыть в Orbit»") {
                    Text("Finder → правый щелчок → Службы").foregroundStyle(.secondary)
                }
            }

            Section("В открытом круге") {
                LabeledContent("Другая клавиша-модификатор", value: "переключает колесо (та же — листает)")
                LabeledContent("Задержать файл над «Ещё»", value: "следующая страница")
                LabeledContent("Бросить файл в центр", value: "круг остаётся — выберите щелчком")
                LabeledContent("Стрелки, Return, Tab, 1–9, Esc", value: "управление с клавиатуры")
            }
        }
        .formStyle(.grouped)
    }
}

private struct OutputTab: View {
    @ObservedObject var settings: Settings

    var body: some View {
        Form {
            Section {
                Picker("Куда сохранять результат", selection: $settings.output) {
                    Text("Рядом с исходным файлом").tag(OutputMode.sameFolder)
                    Text("В подпапку «Orbit»").tag(OutputMode.subfolder)
                    Text("В выбранную папку").tag(OutputMode.custom)
                }
                if settings.output == .custom {
                    LabeledContent("Папка") {
                        HStack {
                            Text(settings.customFolder.isEmpty ? "не выбрана" : settings.customFolder)
                                .lineLimit(1).truncationMode(.middle).foregroundStyle(.secondary)
                            Button("Выбрать…") { chooseFolder() }
                        }
                    }
                }
            } footer: {
                Text("Исходные файлы никогда не перезаписываются: занятое имя получает номер.")
            }

            Section("Качество") {
                LabeledContent("Изображения: \(settings.imageQuality)") {
                    Slider(value: Binding(get: { Double(settings.imageQuality) }, set: { settings.imageQuality = Int($0) }),
                           in: 10...100, step: 1)
                        .frame(width: 220)
                }
                Picker("Видео", selection: $settings.video) {
                    Text("Меньше файл (720p)").tag(VideoQuality.small)
                    Text("Сбалансировано (до 1080p)").tag(VideoQuality.balanced)
                    Text("Высокое (исходное разрешение)").tag(VideoQuality.high)
                }
                Picker("Страницы PDF в картинки", selection: $settings.pdfDpi) {
                    Text("72 точки на дюйм").tag(72)
                    Text("150 точек на дюйм").tag(150)
                    Text("300 точек на дюйм").tag(300)
                }
            }

            Section {
                Picker("Word и Excel", selection: $settings.office) {
                    Text("Автоматически").tag(OfficeEngine.auto)
                    Text("Встроенный в macOS").tag(OfficeEngine.builtIn)
                    Text("LibreOffice").tag(OfficeEngine.libreOffice)
                }
            } header: {
                Text("Документы")
            } footer: {
                Text(OfficeBackend.libreOffice == nil
                     ? "LibreOffice не найден. Встроенный конвертер macOS работает с DOCX, DOC, RTF и ODT; для таблиц установите LibreOffice (libreoffice.org)."
                     : "LibreOffice найден — «Автоматически» конвертирует им документы и таблицы.")
            }
        }
        .formStyle(.grouped)
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Выбрать"
        if panel.runModal() == .OK, let url = panel.url { settings.customFolder = url.path }
    }
}

private struct FormatRow: Identifiable {
    let symbol: String
    let title: String
    let inputs: String
    let outputs: String

    var id: String { title }

    init(_ symbol: String, _ title: String, _ inputs: String, _ outputs: String) {
        self.symbol = symbol
        self.title = title
        self.inputs = inputs
        self.outputs = outputs
    }
}

private struct FormatsTab: View {
    private let rows: [FormatRow] = [
        FormatRow("photo", "Изображения", "PNG, JPG, HEIC, WEBP, AVIF, TIFF, GIF, BMP, PSD, RAW…",
         ImageBackend.targets.map { $0.ext.uppercased() }.joined(separator: ", ")),
        FormatRow("waveform", "Аудио", "MP3, M4A, AAC, WAV, FLAC, AIFF, CAF", "M4A, WAV, AIFF, FLAC"),
        FormatRow("film", "Видео", "MP4, MOV, M4V, 3GP", "MP4, MOV, M4A, WAV"),
        FormatRow("doc.richtext", "PDF", "PDF", "PNG, JPG, TXT · объединить, разделить"),
        FormatRow("doc.text", "Документы", "DOCX, DOC, RTF, ODT", "PDF, DOCX, DOC, RTF, ODT, TXT"),
        FormatRow("tablecells", "Таблицы", "XLSX, XLS, ODS, CSV", "PDF, XLSX, XLS, ODS, CSV (LibreOffice)"),
        FormatRow("archivebox", "Архивы", "ZIP, 7Z, RAR, TAR, GZ, BZ2, XZ", "распаковка · ZIP из любых файлов"),
    ]

    var body: some View {
        Form {
            Section("Что во что конвертируется") {
                ForEach(rows) { row in
                    HStack(alignment: .top, spacing: 12) {
                        Image(systemName: row.symbol).frame(width: 22).foregroundStyle(Color.accentColor)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(row.title).font(.headline)
                            Text(row.inputs).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Text(row.outputs).font(.callout).multilineTextAlignment(.trailing).frame(maxWidth: 240, alignment: .trailing)
                    }
                }
            }
        }
        .formStyle(.grouped)
    }
}

private struct AboutTab: View {
    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "—"
    }

    var body: some View {
        Form {
            Section {
                HStack(spacing: 16) {
                    Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 64, height: 64)
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Orbit \(version)").font(.title2.bold())
                        Text("Конвертер файлов с круговым меню для macOS").foregroundStyle(.secondary)
                    }
                }
                .padding(.vertical, 4)
            }
            Section {
                Label("Файлы не покидают компьютер: всё работает локально и без интернета — ImageIO, AVFoundation, PDFKit и Vision из macOS.",
                      systemImage: "lock.shield")
                LabeledContent("Настройки", value: "~/Library/Preferences")
                Button("Выйти из Orbit") { NSApp.terminate(nil) }
            }
        }
        .formStyle(.grouped)
    }
}
