import AppKit
import QuickLookThumbnailing

enum WheelMode {
    /// Открыто во время перетаскивания: файл отпускают на секторе. Фокус не забирает.
    case drag
    /// Открыто горячей клавишей или из меню: сектор выбирают щелчком или клавиатурой.
    case click
}

final class WheelPanel: NSPanel {
    var onResignKey: (() -> Void)?

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    override func resignKey() {
        super.resignKey()
        onResignKey?()
    }
}

private enum Palette {
    static let disc = NSColor(name: nil) { $0.isDark ? NSColor(white: 0.16, alpha: 0.98) : NSColor(white: 0.98, alpha: 0.98) }
    static let slice = NSColor(name: nil) { $0.isDark ? NSColor(white: 0.24, alpha: 1) : NSColor(white: 0.92, alpha: 1) }
    static let hub = NSColor(name: nil) { $0.isDark ? NSColor(white: 0.20, alpha: 1) : NSColor.white }
    static let stroke = NSColor(name: nil) { $0.isDark ? NSColor(white: 1, alpha: 0.10) : NSColor(white: 0, alpha: 0.08) }
    static let dot = NSColor(name: nil) { $0.isDark ? NSColor(white: 1, alpha: 0.25) : NSColor(white: 0, alpha: 0.2) }
}

private extension NSAppearance {
    var isDark: Bool { bestMatch(from: [.aqua, .darkAqua]) == .darkAqua }
}

/// Круговое меню. Новый экземпляр на каждое появление.
final class WheelView: NSView {
    typealias Source = (WheelKind, [FileItem]) -> [Operation]

    private static let outer: CGFloat = 192
    private static let inner: CGFloat = 74
    private static let disc: CGFloat = outer + 11
    private static let pad: CGFloat = 28
    static let size: CGFloat = 2 * (disc + pad)
    private static let center: CGFloat = size / 2
    private static let gap: CGFloat = 3
    /// Толщина обводки цветом сектора — она и скругляет углы.
    private static let rounding: CGFloat = 8
    private static let popOut: CGFloat = 6
    private static let pageSize = 10

    let panel: WheelPanel
    var onClose: (() -> Void)?

    private(set) var mode: WheelMode
    private(set) var wheel: WheelKind
    private(set) var isFinished = false
    private var items: [FileItem]
    private var ops: [Operation] = []
    private var slices: [Operation?] = []
    private let source: Source
    private let execute: (Operation, [FileItem]) -> Void
    private var page = 0
    private var highlight = -1
    private var dropped = false
    private var thumbnail: NSImage?
    private var flipTask: Task<Void, Never>?
    private var closeTask: Task<Void, Never>?
    private var scrollAccumulator: CGFloat = 0

    init(mode: WheelMode, wheel: WheelKind, items: [FileItem], source: @escaping Source, execute: @escaping (Operation, [FileItem]) -> Void) {
        self.mode = mode
        self.wheel = wheel
        self.items = items
        self.source = source
        self.execute = execute
        panel = WheelPanel(contentRect: NSRect(x: 0, y: 0, width: Self.size, height: Self.size),
                           styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        super.init(frame: NSRect(x: 0, y: 0, width: Self.size, height: Self.size))

        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .popUpMenu
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.acceptsMouseMovedEvents = true
        panel.contentView = self
        panel.onResignKey = { [weak self] in
            if self?.mode == .click { self?.finish() }
        }
        registerForDraggedTypes([.fileURL])
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .activeAlways, .inVisibleRect], owner: self))

        if !items.isEmpty { ops = source(wheel, items) }
        rebuild()
        loadThumbnail()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    private var pageCount: Int {
        ops.count <= Self.pageSize ? 1 : Int((Double(ops.count) / Double(Self.pageSize - 1)).rounded(.up))
    }

    // MARK: показ и закрытие

    func show(at point: NSPoint) {
        let s = Self.size
        var frame = NSRect(x: point.x - s / 2, y: point.y - s / 2, width: s, height: s)
        if let screen = NSScreen.screens.first(where: { NSMouseInRect(point, $0.frame, false) }) {
            // Круг целиком на экране; прозрачные поля вокруг диска могут выходить за край.
            let f = screen.frame.insetBy(dx: -Self.pad, dy: -Self.pad)
            frame.origin.x = min(max(frame.minX, f.minX), f.maxX - s)
            frame.origin.y = min(max(frame.minY, f.minY), f.maxY - s)
        }
        panel.setFrame(frame, display: false)
        panel.alphaValue = 0
        if mode == .click {
            NSApp.activate(ignoringOtherApps: true)
            panel.makeKeyAndOrderFront(nil)
            panel.makeFirstResponder(self)
        } else {
            panel.orderFrontRegardless()
        }
        NSAnimationContext.beginGrouping()
        NSAnimationContext.current.duration = 0.12
        panel.animator().alphaValue = 1
        NSAnimationContext.endGrouping()
    }

    /// Лежит ли экранная точка на круге.
    func contains(_ screenPoint: NSPoint) -> Bool {
        let f = panel.frame
        return hypot(screenPoint.x - f.midX, screenPoint.y - f.midY) <= Self.disc
    }

    func finish() {
        guard !isFinished else { return }
        isFinished = true
        flipTask?.cancel()
        closeTask?.cancel()
        NSAnimationContext.beginGrouping()
        NSAnimationContext.current.duration = 0.1
        panel.animator().alphaValue = 0
        NSAnimationContext.endGrouping()
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 120_000_000)
            self.panel.orderOut(nil)
            self.onClose?()
        }
    }

    /// Отпустили кнопку мыши где угодно: если файл так и не бросили на круг — закрываемся.
    func onDragEnded() {
        guard mode == .drag, !isFinished else { return }
        closeTask?.cancel()
        closeTask = Task { @MainActor in
            try? await Task.sleep(nanoseconds: 400_000_000)
            if !Task.isCancelled && self.mode == .drag && !self.dropped { self.finish() }
        }
    }

    /// Повторное нажатие той же клавиши листает страницы, другая клавиша — переключает колесо.
    func switchTo(_ kind: WheelKind) {
        if kind == wheel {
            flipPage(1)
            return
        }
        wheel = kind
        page = 0
        ops = items.isEmpty ? [] : source(wheel, items)
        rebuild()
    }

    private func enterClickMode() {
        mode = .click
        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil)
        panel.makeFirstResponder(self)
        setHighlight(-1)
    }

    // MARK: секторы

    private func rebuild() {
        if pageCount == 1 {
            slices = ops
        } else {
            let perPage = Self.pageSize - 1
            slices = Array(ops.dropFirst(page * perPage).prefix(perPage)) + [nil]
        }
        highlight = -1
        needsDisplay = true
    }

    private func flipPage(_ delta: Int) {
        guard pageCount > 1, !isFinished else { return }
        page = (page + delta + pageCount) % pageCount
        rebuild()
    }

    private func index(at p: NSPoint) -> (index: Int, onHub: Bool) {
        let dx = p.x - Self.center
        let dy = p.y - Self.center
        let r = hypot(dx, dy)
        if r < Self.inner - 2 { return (-1, true) }
        if r > Self.disc + 6 || slices.isEmpty { return (-1, false) }
        let fromTop = (atan2(dy, dx) * 180 / .pi + 90 + 360).truncatingRemainder(dividingBy: 360)
        let span = 360 / CGFloat(slices.count)
        return (Int(((fromTop + span / 2).truncatingRemainder(dividingBy: 360)) / span) % slices.count, false)
    }

    private func setHighlight(_ index: Int) {
        guard index != highlight else { return }
        highlight = index
        armFlip()
        needsDisplay = true
    }

    /// При перетаскивании «Ещё» листает страницы, если задержать файл над ним.
    private func armFlip() {
        flipTask?.cancel()
        guard mode == .drag, highlight >= 0, highlight < slices.count, slices[highlight] == nil else { return }
        flipTask = Task { @MainActor in
            try? await Task.sleep(nanoseconds: 650_000_000)
            if !Task.isCancelled { self.flipPage(1) }
        }
    }

    private func runSlice(_ index: Int) {
        guard index >= 0, index < slices.count else { return }
        if let op = slices[index] {
            finish()
            execute(op, items)
        } else {
            flipPage(1)
        }
    }

    // MARK: рисование

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        let c = Self.center
        let disc = Self.disc

        ctx.saveGState()
        ctx.setShadow(offset: CGSize(width: 0, height: -6), blur: 28, color: NSColor.black.withAlphaComponent(0.3).cgColor)
        ctx.setFillColor(Palette.disc.cgColor)
        ctx.fillEllipse(in: CGRect(x: c - disc, y: c - disc, width: 2 * disc, height: 2 * disc))
        ctx.restoreGState()
        ctx.setStrokeColor(Palette.stroke.cgColor)
        ctx.setLineWidth(1)
        ctx.strokeEllipse(in: CGRect(x: c - disc, y: c - disc, width: 2 * disc, height: 2 * disc))

        let n = slices.count
        let span = 2 * CGFloat.pi / CGFloat(max(1, n))
        for i in 0..<n {
            let mid = -CGFloat.pi / 2 + CGFloat(i) * span
            let lit = i == highlight
            ctx.saveGState()
            if lit && n > 1 { ctx.translateBy(x: Self.popOut * cos(mid), y: Self.popOut * sin(mid)) }
            let color = lit ? NSColor.controlAccentColor : Palette.slice
            ctx.setFillColor(color.cgColor)
            if n == 1 {
                ctx.addPath(ringPath())
                ctx.fillPath(using: .evenOdd)
            } else {
                ctx.addPath(slicePath(from: mid - span / 2, to: mid + span / 2))
                ctx.setStrokeColor(color.cgColor)
                ctx.setLineWidth(Self.rounding)
                ctx.setLineJoin(.round)
                ctx.drawPath(using: .fillStroke)
            }
            drawLabel(slices[i], mid: mid, span: span, count: n, lit: lit)
            ctx.restoreGState()
        }

        let hubRadius = Self.inner - 6
        ctx.setFillColor(Palette.hub.cgColor)
        ctx.fillEllipse(in: CGRect(x: c - hubRadius, y: c - hubRadius, width: 2 * hubRadius, height: 2 * hubRadius))
        ctx.setStrokeColor(Palette.stroke.cgColor)
        ctx.setLineWidth(1)
        ctx.strokeEllipse(in: CGRect(x: c - hubRadius, y: c - hubRadius, width: 2 * hubRadius, height: 2 * hubRadius))
        drawHub()
    }

    /// Сектор кольца с зазором постоянной ширины; углы скругляет обводка толщиной `rounding`.
    private func slicePath(from a0: CGFloat, to a1: CGFloat) -> CGPath {
        let inset = Self.rounding / 2
        let rOut = Self.outer - inset
        let rIn = Self.inner + inset
        let halfGap = Self.gap / 2 + inset
        let gOut = asin(halfGap / rOut)
        let gIn = asin(halfGap / rIn)
        let c = CGPoint(x: Self.center, y: Self.center)
        let path = CGMutablePath()
        path.addArc(center: c, radius: rOut, startAngle: a0 + gOut, endAngle: a1 - gOut, clockwise: false)
        path.addArc(center: c, radius: rIn, startAngle: a1 - gIn, endAngle: a0 + gIn, clockwise: true)
        path.closeSubpath()
        return path
    }

    private func ringPath() -> CGPath {
        let c = Self.center
        let path = CGMutablePath()
        path.addEllipse(in: CGRect(x: c - Self.outer, y: c - Self.outer, width: 2 * Self.outer, height: 2 * Self.outer))
        path.addEllipse(in: CGRect(x: c - Self.inner, y: c - Self.inner, width: 2 * Self.inner, height: 2 * Self.inner))
        return path
    }

    private static func symbol(_ name: String, size: CGFloat, color: NSColor) -> NSImage? {
        let config = NSImage.SymbolConfiguration(pointSize: size, weight: .medium)
            .applying(NSImage.SymbolConfiguration(paletteColors: [color]))
        let image = NSImage(systemSymbolName: name, accessibilityDescription: nil)
            ?? NSImage(systemSymbolName: "circle", accessibilityDescription: nil)
        return image?.withSymbolConfiguration(config)
    }

    private static func attributes(_ size: CGFloat, _ weight: NSFont.Weight, _ color: NSColor) -> [NSAttributedString.Key: Any] {
        let style = NSMutableParagraphStyle()
        style.alignment = .center
        style.lineBreakMode = .byTruncatingTail
        return [.font: NSFont.systemFont(ofSize: size, weight: weight), .foregroundColor: color, .paragraphStyle: style]
    }

    private func drawLabel(_ op: Operation?, mid: CGFloat, span: CGFloat, count: Int, lit: Bool) {
        let midR = (Self.inner + Self.outer) / 2
        let width: CGFloat = count <= 1 ? 120 : min(104, 2 * midR * sin(span / 2) - 10)
        let primary: NSColor = lit ? .white : .labelColor
        let secondary: NSColor = lit ? NSColor.white.withAlphaComponent(0.85) : .secondaryLabelColor
        let accent: NSColor = lit ? .white : .controlAccentColor

        let label = NSAttributedString(string: op?.label ?? "Ещё", attributes: Self.attributes(12.5, .semibold, primary))
        let subText = op == nil ? "\(page + 1) из \(pageCount)" : op?.subtitle
        let sub = subText.map { NSAttributedString(string: $0, attributes: Self.attributes(10.5, .regular, secondary)) }
        let labelHeight = min(ceil(label.boundingRect(with: NSSize(width: width, height: 34), options: [.usesLineFragmentOrigin]).height), 34)
        let subHeight: CGFloat = sub == nil ? 0 : 14
        let icon = Self.symbol(op?.symbol ?? Symbols.more, size: 17, color: accent)
        let iconHeight: CGFloat = 20
        let total = iconHeight + 3 + labelHeight + subHeight

        let cx = Self.center + midR * cos(mid)
        var y = Self.center + midR * sin(mid) - total / 2
        if let icon {
            let s = icon.size
            icon.draw(in: NSRect(x: cx - s.width / 2, y: y + (iconHeight - s.height) / 2, width: s.width, height: s.height),
                      from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
        }
        y += iconHeight + 3
        label.draw(with: NSRect(x: cx - width / 2, y: y, width: width, height: labelHeight),
                   options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine])
        y += labelHeight
        sub?.draw(with: NSRect(x: cx - width / 2, y: y, width: width, height: subHeight),
                  options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine])
    }

    // MARK: центр круга

    private func drawHub() {
        let slice: Operation?? = highlight >= 0 && highlight < slices.count ? .some(slices[highlight]) : .none
        let title: String
        let detail: String
        if let slice {
            if let op = slice {
                title = op.label
                detail = op.subtitle ?? wheel.title
            } else {
                title = "Ещё"
                detail = mode == .drag ? "задержите, чтобы листать" : "страница \(page + 1) из \(pageCount)"
            }
        } else if items.isEmpty {
            title = wheel.title
            detail = "Отпустите файл на действии"
        } else if ops.isEmpty {
            title = "Нет действий"
            detail = wheel == .convert ? "Попробуйте Control или Option" : "Попробуйте другое колесо"
        } else {
            title = filesCaption(items)
            detail = "\(wheel.title) · \(wheel.keyName)"
        }

        let width = (Self.inner - 6) * 1.6
        let titleText = NSAttributedString(string: title, attributes: Self.attributes(13, .semibold, .labelColor))
        let detailText = NSAttributedString(string: detail, attributes: Self.attributes(10.5, .regular, .secondaryLabelColor))
        let picture: NSImage? = slice == nil && !items.isEmpty ? thumbnail : nil
        let glyph: NSImage? = picture == nil
            ? Self.symbol(slice.map { $0?.symbol ?? Symbols.more } ?? wheel.symbol, size: 24, color: .controlAccentColor)
            : nil
        let iconHeight: CGFloat = picture != nil ? 40 : 28
        let total = iconHeight + 6 + 16 + 14 + 8 + 6
        var y = Self.center - total / 2
        let c = Self.center
        if let picture {
            picture.draw(in: NSRect(x: c - 20, y: y, width: 40, height: 40), from: .zero, operation: .sourceOver,
                         fraction: 1, respectFlipped: true, hints: nil)
        } else if let glyph {
            let s = glyph.size
            glyph.draw(in: NSRect(x: c - s.width / 2, y: y + (iconHeight - s.height) / 2, width: s.width, height: s.height),
                       from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
        }
        y += iconHeight + 6
        titleText.draw(with: NSRect(x: c - width / 2, y: y, width: width, height: 16), options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine])
        y += 16
        detailText.draw(with: NSRect(x: c - width / 2, y: y, width: width, height: 14), options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine])
        y += 14 + 8

        // Точки-индикаторы трёх колёс.
        let kinds = WheelKind.allCases
        let widths = kinds.map { $0 == wheel ? CGFloat(16) : CGFloat(6) }
        var x = c - (widths.reduce(0, +) + CGFloat(kinds.count - 1) * 5) / 2
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        for (kind, w) in zip(kinds, widths) {
            ctx.setFillColor((kind == wheel ? NSColor.controlAccentColor : Palette.dot).cgColor)
            ctx.addPath(CGPath(roundedRect: CGRect(x: x, y: y, width: w, height: 6), cornerWidth: 3, cornerHeight: 3, transform: nil))
            ctx.fillPath()
            x += w + 5
        }
    }

    private func loadThumbnail() {
        guard thumbnail == nil, let first = items.first else { return }
        thumbnail = NSWorkspace.shared.icon(forFile: first.path)
        let request = QLThumbnailGenerator.Request(fileAt: first.url, size: CGSize(width: 40, height: 40),
                                                   scale: NSScreen.main?.backingScaleFactor ?? 2, representationTypes: .thumbnail)
        QLThumbnailGenerator.shared.generateBestRepresentation(for: request) { [weak self] representation, _ in
            guard let image = representation?.nsImage else { return }
            Task { @MainActor in
                self?.thumbnail = image
                self?.needsDisplay = true
            }
        }
    }

    // MARK: мышь и клавиатура

    private func location(_ event: NSEvent) -> NSPoint { convert(event.locationInWindow, from: nil) }

    override func mouseMoved(with event: NSEvent) {
        if mode == .click { setHighlight(index(at: location(event)).index) }
    }

    override func mouseUp(with event: NSEvent) {
        guard mode == .click, !isFinished else { return }
        let hit = index(at: location(event))
        if hit.onHub { return }
        if hit.index < 0 { finish() } else { runSlice(hit.index) }
    }

    override func rightMouseUp(with event: NSEvent) { finish() }

    override func scrollWheel(with event: NSEvent) {
        scrollAccumulator += event.scrollingDeltaY
        if abs(scrollAccumulator) > 12 {
            flipPage(scrollAccumulator < 0 ? 1 : -1)
            scrollAccumulator = 0
        }
    }

    override func keyDown(with event: NSEvent) {
        guard mode == .click, !slices.isEmpty else {
            if event.keyCode == 53 { finish() }
            return
        }
        let n = slices.count
        switch event.keyCode {
        case 53: finish()
        case 36, 76: if highlight >= 0 { runSlice(highlight) }
        case 48: setHighlight(event.modifierFlags.contains(.shift) ? (highlight - 1 + n) % n : (highlight + 1) % n)
        case 123, 126: setHighlight(highlight < 0 ? n - 1 : (highlight - 1 + n) % n)
        case 124, 125: setHighlight((highlight + 1) % n)
        default:
            if let ch = event.charactersIgnoringModifiers?.first, let digit = ch.wholeNumberValue, digit >= 1, digit <= min(9, n) {
                runSlice(digit - 1)
            }
        }
    }

    // MARK: перетаскивание

    /// Только копирование или ссылка — «перемещение» заставило бы Finder убрать исходный файл.
    private func safeOperation(_ allowed: NSDragOperation) -> NSDragOperation {
        if allowed.contains(.copy) { return .copy }
        if allowed.contains(.link) { return .link }
        if allowed.contains(.generic) { return .generic }
        return []
    }

    private func adopt(_ pasteboard: NSPasteboard) {
        let objects = pasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) ?? []
        let urls = objects.compactMap { ($0 as? NSURL) as URL? }
        guard !urls.isEmpty, urls.map(\.standardizedFileURL) != items.map(\.url) else { return }
        items = urls.map(FileItem.init)
        ops = source(wheel, items)
        page = 0
        thumbnail = nil
        rebuild()
        loadThumbnail()
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        guard !isFinished else { return [] }
        adopt(sender.draggingPasteboard)
        return draggingUpdated(sender)
    }

    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        guard !isFinished else { return [] }
        setHighlight(index(at: convert(sender.draggingLocation, from: nil)).index)
        return safeOperation(sender.draggingSourceOperationMask)
    }

    override func draggingExited(_ sender: NSDraggingInfo?) {
        setHighlight(-1)
    }

    override func prepareForDragOperation(_ sender: NSDraggingInfo) -> Bool { !isFinished }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        guard !isFinished else { return false }
        adopt(sender.draggingPasteboard)
        dropped = true
        closeTask?.cancel()
        let hit = index(at: convert(sender.draggingLocation, from: nil))
        if hit.index >= 0, let op = slices[hit.index] {
            finish()
            // Finder ждёт ответа на бросок — операцию запускаем уже после него.
            let targets = items
            Task { @MainActor in self.execute(op, targets) }
        } else {
            // Брошено в центр или на «Ещё»: круг остаётся, пункт выбирают щелчком.
            if hit.index >= 0 { flipPage(1) }
            Task { @MainActor in self.enterClickMode() }
        }
        return true
    }
}
