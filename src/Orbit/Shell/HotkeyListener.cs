using System.Windows.Interop;

namespace Orbit.Shell;

/// <summary>Глобальная горячая клавиша Ctrl + Alt + Пробел через невидимое окно сообщений.</summary>
internal sealed class HotkeyListener : IDisposable
{
    private const int Id = 0x0B17;
    private const uint VK_SPACE = 0x20;
    private readonly HwndSource _source;
    private bool _registered;

    public HotkeyListener()
    {
        _source = new HwndSource(new HwndSourceParameters("Orbit hotkey") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _source.AddHook(WndProc);
    }

    public event Action? Pressed;

    /// <summary>Включает или выключает клавишу; false, если её уже заняло другое приложение.</summary>
    public bool SetEnabled(bool enabled)
    {
        if (enabled == _registered)
            return true;
        if (enabled)
            _registered = Native.RegisterHotKey(_source.Handle, Id, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VK_SPACE);
        else
            _registered = !Native.UnregisterHotKey(_source.Handle, Id) && _registered;
        return _registered == enabled;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        SetEnabled(false);
        _source.Dispose();
    }
}
