using System.IO.Pipes;
using System.Text;
using System.Windows.Threading;

namespace Orbit.Shell;

/// <summary>
/// Держит одну копию программы. Следующие запуски (из меню «Пуск», контекстного меню Проводника, установщика)
/// передают свои аргументы запущенной копии по именованному каналу и завершаются.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    // Имена каналов общие для всей машины, поэтому в имя входит пользователь.
    private static readonly string Id = "Orbit-" + new string(Environment.UserName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + Id, out var created);
        if (created)
            return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public static bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(3000);
            // Запущенная копия покажет окно поверх остальных — разрешаем ей это, ведь запуск сделал пользователь.
            if (Native.GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var pid))
                Native.AllowSetForegroundWindow((int)pid);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(string.Join("\n", args.Length == 0 ? ["--show"] : args));
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Listen(Dispatcher dispatcher, Action<string[]> onArgs)
    {
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(_cts.Token);
                    var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    _ = dispatcher.BeginInvoke(() => onArgs(args));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(200);
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        _mutex.Dispose();
    }
}
