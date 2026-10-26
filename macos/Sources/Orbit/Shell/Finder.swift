import AppKit

enum Finder {
    static let bundleID = "com.apple.finder"

    static var isFrontmost: Bool { NSWorkspace.shared.frontmostApplication?.bundleIdentifier == bundleID }

    /// Принадлежит ли окно под точкой (глобальные координаты, начало в левом верхнем углу) Finder — его окну или рабочему столу.
    /// Названия окон без разрешения на запись экрана скрыты, но владелец и границы видны всегда.
    static func ownsWindow(at point: CGPoint) -> Bool {
        guard let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] else { return false }
        let me = ProcessInfo.processInfo.processIdentifier
        for window in windows {
            guard let pid = window[kCGWindowOwnerPID as String] as? pid_t, pid != me,
                  let boundsDict = window[kCGWindowBounds as String] as? NSDictionary,
                  let bounds = CGRect(dictionaryRepresentation: boundsDict as CFDictionary),
                  bounds.contains(point) else { continue }
            if let alpha = window[kCGWindowAlpha as String] as? Double, alpha <= 0 { continue }
            if let layer = window[kCGWindowLayer as String] as? Int, layer >= 1000 { continue }
            return NSRunningApplication(processIdentifier: pid)?.bundleIdentifier == bundleID
        }
        return false
    }

    /// Файлы, выделенные в переднем окне Finder (или на рабочем столе). Нужен доступ к «Автоматизации» Finder.
    @MainActor
    static func selection() -> [URL] {
        let source = """
        tell application "Finder"
            set out to ""
            repeat with f in (get selection as alias list)
                set out to out & POSIX path of f & linefeed
            end repeat
            return out
        end tell
        """
        var error: NSDictionary?
        guard let result = NSAppleScript(source: source)?.executeAndReturnError(&error).stringValue else { return [] }
        return result.split(separator: "\n").map { URL(fileURLWithPath: String($0)) }
    }
}
