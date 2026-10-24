using System.Runtime.InteropServices;
using System.Windows.Threading;
using Orbit.Core;

namespace Orbit.Shell;

/// <summary>
/// Глобальные хуки мыши и клавиатуры в отдельном потоке. Ловит жест «во время перетаскивания нажали и отпустили
/// Shift / Ctrl / Alt» и передаёт события в UI-поток.
/// </summary>
internal sealed class GlobalInput : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Native.HookProc _keyboardProc;
    private readonly Native.HookProc _mouseProc;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;

    private bool _leftDown;
    private Native.POINT _downAt;
    private volatile bool _dragging;
    private ModifierKey _pending;
    private int _x;
    private int _y;

    public GlobalInput(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        // Делегаты держим в полях, иначе сборщик мусора уберёт их, пока хук ещё установлен.
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    /// <summary>Модификатор нажали и отпустили отдельно, пока тянется файл.</summary>
    public event Action<ModifierKey>? ModifierTapped;

    public event Action? LeftButtonUp;

    public event Action<Native.POINT>? MouseDown;

    public event Action? EscapePressed;

    /// <summary>Вызывается в потоке хука: открывает ли эта клавиша колесо (для подавления меню по Alt).</summary>
    public Func<ModifierKey, bool>? IsWheelKey { get; set; }

    public bool IsDragging => _dragging;

    public Native.POINT Cursor => new() { X = Volatile.Read(ref _x), Y = Volatile.Read(ref _y) };

    public void Start()
    {
        if (_thread is not null)
            return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            var module = Native.GetModuleHandle(null);
            _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
            ready.Set();
            while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
                // Низкоуровневые хуки обслуживаются, пока поток ждёт сообщений.
            }
            Native.UnhookWindowsHookEx(_keyboardHook);
            Native.UnhookWindowsHookEx(_mouseHook);
        })
        {
            IsBackground = true,
            Name = "Orbit input hooks",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait();
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            throw new InvalidOperationException("Не удалось установить глобальные обработчики клавиатуры и мыши.");
    }

    public void Dispose()
    {
        if (_thread is null)
            return;
        Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
    }

    private static ModifierKey ToModifier(uint vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => ModifierKey.Shift,
        0x11 or 0xA2 or 0xA3 => ModifierKey.Ctrl,
        0x12 or 0xA4 or 0xA5 => ModifierKey.Alt,
        _ => ModifierKey.None,
    };

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                // Синтетические нажатия (наши и чужие) не считаются: жест делает человек.
                if ((data.flags & Native.LLKHF_INJECTED) == 0)
                    OnKey(data.vkCode, (int)wParam is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN);
            }
            catch
            {
                // Исключение не должно уйти в системную цепочку хуков.
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void OnKey(uint vk, bool down)
    {
        var modifier = ToModifier(vk);
        if (down)
        {
            if (modifier == ModifierKey.None)
            {
                _pending = ModifierKey.None;
                if (vk == 0x1B)
                    _dispatcher.BeginInvoke(() => EscapePressed?.Invoke());
                return;
            }
            if (_pending == modifier)
                return; // автоповтор
            _pending = modifier;
            if (modifier == ModifierKey.Alt && _dragging && IsWheelKey?.Invoke(modifier) == true)
            {
                // Одиночный Alt открыл бы меню окна под курсором — «разбавляем» его пустой клавишей.
                Native.keybd_event(Native.VK_UNASSIGNED, 0, 0, UIntPtr.Zero);
                Native.keybd_event(Native.VK_UNASSIGNED, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            return;
        }

        if (modifier != ModifierKey.None && modifier == _pending)
        {
            _pending = ModifierKey.None;
            if (_dragging)
                _dispatcher.BeginInvoke(() => ModifierTapped?.Invoke(modifier));
        }
        else if (modifier == ModifierKey.None)
        {
            _pending = ModifierKey.None;
        }
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                Volatile.Write(ref _x, data.pt.X);
                Volatile.Write(ref _y, data.pt.Y);
                switch ((int)wParam)
                {
                    case Native.WM_LBUTTONDOWN:
                        _leftDown = true;
                        _dragging = false;
                        _downAt = data.pt;
                        RaiseDown(data.pt);
                        break;
                    case Native.WM_RBUTTONDOWN:
                    case Native.WM_MBUTTONDOWN:
                        RaiseDown(data.pt);
                        break;
                    case Native.WM_MOUSEMOVE when _leftDown && !_dragging:
                        var dx = Math.Abs(data.pt.X - _downAt.X);
                        var dy = Math.Abs(data.pt.Y - _downAt.Y);
                        if (dx > Native.GetSystemMetrics(Native.SM_CXDRAG) * 2 || dy > Native.GetSystemMetrics(Native.SM_CYDRAG) * 2)
                            _dragging = true;
                        break;
                    case Native.WM_LBUTTONUP:
                        _leftDown = false;
                        _dragging = false;
                        _dispatcher.BeginInvoke(() => LeftButtonUp?.Invoke());
                        break;
                }
            }
            catch
            {
                // Исключение не должно уйти в системную цепочку хуков.
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void RaiseDown(Native.POINT pt) => _dispatcher.BeginInvoke(() => MouseDown?.Invoke(pt));
}
