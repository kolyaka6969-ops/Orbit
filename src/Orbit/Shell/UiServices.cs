using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;

namespace Orbit.Shell;

/// <summary>Буфер обмена и диалоги работают только в UI-потоке — операции зовут их отсюда из любого потока.</summary>
internal static class UiServices
{
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    public static void SetClipboardText(string text) => OnUi(() => Retry(() => Clipboard.SetText(text)));

    public static void SetClipboardFiles(IEnumerable<string> paths)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        OnUi(() => Retry(() => Clipboard.SetFileDropList(list)));
    }

    public static string? PickFolder(string title)
    {
        string? result = null;
        OnUi(() =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
            if (dialog.ShowDialog() == true)
                result = dialog.FolderName;
        });
        return result;
    }

    /// <summary>Буфер обмена бывает занят другим приложением — пробуем несколько раз.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(60);
            }
        }
    }
}
