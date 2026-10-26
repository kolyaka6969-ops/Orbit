import AppKit

/// Глобальный перехват мыши и клавиатуры (CGEventTap, только чтение). Ловит жест «во время перетаскивания
/// нажали и отпустили Shift / Control / Option» — только если тянут из Finder или с рабочего стола.
/// Нужно разрешение «Мониторинг ввода» в настройках конфиденциальности.
///
/// Перехват работает в главном потоке (источник в главном цикле событий), а события кругу передаются через
/// задачи на главном акторе.
final class GlobalInput {
    /// Модификатор нажали и отпустили отдельно, пока тянется файл или открыт круг.
    var onModifierTap: (@MainActor (ModifierKey) -> Void)?
    var onLeftMouseUp: (@MainActor () -> Void)?
    /// Нажатие любой кнопки мыши — координаты в системе AppKit (начало слева внизу).
    var onMouseDown: (@MainActor (NSPoint) -> Void)?
    var onEscape: (@MainActor () -> Void)?

    /// Нужны ли кругу щелчки, отпускание кнопки и Esc.
    var wheelActive = false

    private(set) var isDragging = false
    private var leftDown = false
    private var checked = false
    private var downAt = CGPoint.zero
    private var dragChangeCount = 0
    private var pending: ModifierKey?
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?

    var isRunning: Bool { tap != nil }

    static var hasPermission: Bool { CGPreflightListenEventAccess() }

    static func requestPermission() { CGRequestListenEventAccess() }

    @discardableResult
    func start() -> Bool {
        if tap != nil { return true }
        let types: [CGEventType] = [.leftMouseDown, .leftMouseUp, .leftMouseDragged, .rightMouseDown, .otherMouseDown, .flagsChanged, .keyDown]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << CGEventMask($1.rawValue)) }
        let refcon = Unmanaged.passUnretained(self).toOpaque()
        guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .listenOnly,
                                          eventsOfInterest: mask, callback: { _, type, event, refcon in
            if let refcon {
                Unmanaged<GlobalInput>.fromOpaque(refcon).takeUnretainedValue().handle(type, event)
            }
            return Unmanaged.passUnretained(event)
        }, userInfo: refcon) else { return false }
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        self.tap = tap
        self.source = source
        return true
    }

    /// Файлы, которые сейчас перетаскивают (буфер перетаскивания изменился после нажатия кнопки).
    func draggedFiles() -> [URL]? {
        let pb = NSPasteboard(name: .drag)
        guard pb.changeCount != dragChangeCount else { return nil }
        let objects = pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) ?? []
        let urls = objects.compactMap { ($0 as? NSURL) as URL? }
        return urls.isEmpty ? nil : urls
    }

    private func handle(_ type: CGEventType, _ event: CGEvent) {
        switch type {
        case .tapDisabledByTimeout, .tapDisabledByUserInput:
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
        case .leftMouseDown:
            leftDown = true
            isDragging = false
            checked = false
            downAt = event.location
            dragChangeCount = NSPasteboard(name: .drag).changeCount
            raiseMouseDown(event)
        case .rightMouseDown, .otherMouseDown:
            raiseMouseDown(event)
        case .leftMouseDragged:
            guard leftDown, !checked else { break }
            let p = event.location
            if abs(p.x - downAt.x) > 4 || abs(p.y - downAt.y) > 4 {
                // В других приложениях модификаторы при перетаскивании значат своё — проверяем один раз за жест.
                checked = true
                isDragging = Finder.ownsWindow(at: downAt)
            }
        case .leftMouseUp:
            leftDown = false
            isDragging = false
            if wheelActive {
                let callback = onLeftMouseUp
                Task { @MainActor in callback?() }
            }
        case .flagsChanged:
            onFlags(event)
        case .keyDown:
            pending = nil
            if wheelActive && event.getIntegerValueField(.keyboardEventKeycode) == 53 {
                let callback = onEscape
                Task { @MainActor in callback?() }
            }
        default:
            break
        }
    }

    private func onFlags(_ event: CGEvent) {
        let key: ModifierKey?
        switch event.getIntegerValueField(.keyboardEventKeycode) {
        case 56, 60: key = .shift
        case 59, 62: key = .control
        case 58, 61: key = .option
        default: key = nil
        }
        guard let key else {
            pending = nil
            return
        }
        let flags = event.flags
        let isDown: Bool
        switch key {
        case .shift: isDown = flags.contains(.maskShift)
        case .control: isDown = flags.contains(.maskControl)
        case .option: isDown = flags.contains(.maskAlternate)
        }
        let others = flags.intersection([.maskShift, .maskControl, .maskAlternate, .maskCommand]).subtracting(mask(of: key))
        if isDown {
            pending = others.isEmpty ? key : nil
        } else if pending == key {
            pending = nil
            if isDragging || wheelActive {
                let callback = onModifierTap
                Task { @MainActor in callback?(key) }
            }
        } else {
            pending = nil
        }
    }

    private func mask(of key: ModifierKey) -> CGEventFlags {
        switch key {
        case .shift: return .maskShift
        case .control: return .maskControl
        case .option: return .maskAlternate
        }
    }

    private func raiseMouseDown(_ event: CGEvent) {
        guard wheelActive else { return }
        let callback = onMouseDown
        // Глобальные координаты Quartz → AppKit: ось Y отсчитывается от низа главного экрана.
        let p = event.location
        let point = NSPoint(x: p.x, y: CGDisplayBounds(CGMainDisplayID()).height - p.y)
        Task { @MainActor in callback?(point) }
    }
}
