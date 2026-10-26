import Carbon.HIToolbox

/// Горячая клавиша ⌃⌥Пробел через Carbon: разрешений не требует.
final class HotKey {
    var onPress: (@MainActor () -> Void)?

    private var ref: EventHotKeyRef?
    private var handler: EventHandlerRef?

    func register() {
        if handler == nil {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), { _, _, userData in
                if let userData {
                    let callback = Unmanaged<HotKey>.fromOpaque(userData).takeUnretainedValue().onPress
                    Task { @MainActor in callback?() }
                }
                return noErr
            }, 1, &spec, Unmanaged.passUnretained(self).toOpaque(), &handler)
        }
        guard ref == nil else { return }
        let id = EventHotKeyID(signature: OSType(0x4F52_4254), id: 1) // «ORBT»
        RegisterEventHotKey(UInt32(kVK_Space), UInt32(controlKey | optionKey), id, GetApplicationEventTarget(), 0, &ref)
    }

    func unregister() {
        if let ref {
            UnregisterEventHotKey(ref)
            self.ref = nil
        }
    }
}
