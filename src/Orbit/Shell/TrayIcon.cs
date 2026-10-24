using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Orbit.Shell;

/// <summary>
/// Значок в области уведомлений: щелчок — настройки, правая кнопка — меню. Напрямую через Shell_NotifyIcon:
/// WinForms ради одного NotifyIcon добавлял к программе ~24 МБ библиотек и несколько МБ памяти.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;

    // После перезапуска Проводника значки нужно добавить заново — он сообщает об этом широковещательно.
    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private string _tip = "Orbit";

    public TrayIcon()
    {
        // Невидимое окно верхнего уровня (не HWND_MESSAGE): иначе не дойдёт сообщение TaskbarCreated.
        _window = new HwndSource(new HwndSourceParameters("Orbit tray") { WindowStyle = 0, Width = 0, Height = 0 });
        _window.AddHook(WndProc);
        var small = new IntPtr[1];
        _icon = ExtractIconEx(Environment.ProcessPath!, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero ? small[0] : LoadIcon(IntPtr.Zero, new IntPtr(32512));
        Update(NIM_ADD);
    }

    public event Action? Clicked;

    public event Action? MenuRequested;

    public void SetPaused(bool paused)
    {
        _tip = paused ? "Orbit — приостановлен" : "Orbit";
        Update(NIM_MODIFY);
    }

    private bool Update(uint message)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tip,
        };
        return Shell_NotifyIcon(message, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            handled = true;
            switch ((int)lParam.ToInt64() & 0xFFFF)
            {
                case WM_LBUTTONUP:
                    Clicked?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    MenuRequested?.Invoke();
                    break;
            }
        }
        else if (msg == TaskbarCreated && TaskbarCreated != 0)
        {
            Update(NIM_ADD);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Update(NIM_DELETE);
        DestroyIcon(_icon);
        _window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);
}
