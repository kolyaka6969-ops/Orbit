import AppKit

@main
enum Entry {
    /// Orbit живёт в строке меню: без значка в Dock (LSUIElement в Info.plist).
    @MainActor
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.run()
    }
}
