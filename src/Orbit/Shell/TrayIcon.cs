using System.Windows;
using Forms = System.Windows.Forms;

namespace Orbit.Shell;

/// <summary>Значок в области уведомлений: щелчок — настройки, правая кнопка — меню.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _normal;

    public TrayIcon()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Orbit;component/Assets/Orbit.ico"))!.Stream;
        _normal = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        _icon = new Forms.NotifyIcon { Icon = _normal, Text = "Orbit", Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
                MenuRequested?.Invoke();
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                Clicked?.Invoke();
        };
    }

    public event Action? Clicked;

    public event Action? MenuRequested;

    public void SetPaused(bool paused) => _icon.Text = paused ? "Orbit — приостановлен" : "Orbit";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _normal.Dispose();
    }
}
