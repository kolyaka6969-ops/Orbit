using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Orbit.Core;

namespace Orbit.UI;

public enum JobState
{
    Running,
    Done,
    Failed,
    Canceled,

    /// <summary>Просто сообщение, без операции.</summary>
    Info,
}

/// <summary>Одна запущенная операция — строка во всплывающей панели.</summary>
public sealed class Job : INotifyPropertyChanged
{
    private double _progress;
    private JobState _state;
    private string _detail = "Выполняется…";

    public Job(Operation op, IReadOnlyList<FileItem> items)
    {
        Operation = op;
        Items = items;
        var label = op.Subtitle is { Length: > 0 } && op.Wheel == WheelKind.Convert ? $"{op.Label} · {op.Subtitle}" : op.Label;
        Title = $"{label} — {(items.Count == 1 ? items[0].Name : $"{items.Count} {Plural(items.Count, "файл", "файла", "файлов")}")}";
    }

    private Job(string title, string detail)
    {
        Operation = InfoOperation;
        Items = [];
        Title = title;
        _detail = detail;
        _state = JobState.Info;
    }

    private static readonly Operation InfoOperation = new()
    {
        Id = "info",
        Wheel = WheelKind.Actions,
        Label = "",
        Glyph = Glyphs.Info,
        AppliesTo = _ => false,
        Run = (_, _) => Task.FromResult<IReadOnlyList<string>>([]),
    };

    public static Job Info(string title, string detail) => new(title, detail);

    public Operation Operation { get; }

    public IReadOnlyList<FileItem> Items { get; }

    public string Title { get; }

    public string Glyph => Operation.Glyph;

    public CancellationTokenSource Cancellation { get; } = new();

    public IReadOnlyList<string> Outputs { get; private set; } = [];

    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(value - _progress) < 0.004)
                return;
            _progress = value;
            OnChanged();
            OnChanged(nameof(IsIndeterminate));
        }
    }

    public bool IsIndeterminate => _state == JobState.Running && _progress <= 0;

    public JobState State
    {
        get => _state;
        private set
        {
            _state = value;
            OnChanged();
            OnChanged(nameof(IsRunning));
            OnChanged(nameof(IsIndeterminate));
            OnChanged(nameof(CanReveal));
        }
    }

    public bool IsRunning => _state == JobState.Running;

    public bool CanReveal => _state == JobState.Done && Outputs.Count > 0;

    public string Detail
    {
        get => _detail;
        private set
        {
            _detail = value;
            OnChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Complete(IReadOnlyList<string> outputs, string? message)
    {
        Outputs = outputs;
        Progress = 1;
        Detail = message ?? (outputs.Count switch
        {
            0 => "Готово",
            1 => $"Готово: {Path.GetFileName(outputs[0])}",
            _ => $"Готово: {outputs.Count} {Plural(outputs.Count, "файл", "файла", "файлов")}",
        });
        State = JobState.Done;
    }

    public void Fail(string message)
    {
        Detail = message;
        State = JobState.Failed;
    }

    public void Cancel()
    {
        Detail = "Отменено";
        State = JobState.Canceled;
    }

    /// <summary>Открывает Проводник с выделенным результатом.</summary>
    public void Reveal()
    {
        if (Outputs.Count == 0)
            return;
        var target = Outputs[0];
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        return mod100 is >= 11 and <= 14 ? many : mod10 == 1 ? one : mod10 is >= 2 and <= 4 ? few : many;
    }
}

/// <summary>Запускает операции в фоне (не больше двух тяжёлых одновременно) и показывает их в панели.</summary>
internal sealed class JobRunner
{
    private readonly Settings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _slots = new(2);

    public JobRunner(Settings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
    }

    public ObservableCollection<Job> Jobs { get; } = [];

    public event Action<Job>? Started;

    public event Action<Job>? Finished;

    public Job Start(Operation op, IReadOnlyList<FileItem> items)
    {
        var job = new Job(op, items);
        Add(job);
        _ = RunAsync(job);
        return job;
    }

    public void Notify(string title, string text) => Add(Job.Info(title, text));

    private void Add(Job job)
    {
        Jobs.Insert(0, job);
        while (Jobs.Count > 6 && Jobs.LastOrDefault(j => !j.IsRunning) is { } old)
            Jobs.Remove(old);
        Started?.Invoke(job);
    }

    private async Task RunAsync(Job job)
    {
        var token = job.Cancellation.Token;
        var context = new JobContext
        {
            Settings = _settings,
            Token = token,
            Progress = new Reporter(_dispatcher, job),
        };
        try
        {
            IReadOnlyList<string> outputs;
            if (job.Operation.Instant)
            {
                outputs = await job.Operation.Run(job.Items, context);
            }
            else
            {
                await _slots.WaitAsync(token);
                try
                {
                    outputs = await Task.Run(() => job.Operation.Run(job.Items, context), token);
                }
                finally
                {
                    _slots.Release();
                }
            }
            job.Complete(outputs, context.Message);
        }
        catch (OperationCanceledException)
        {
            job.Cancel();
        }
        catch (Exception ex)
        {
            job.Fail(Describe(ex));
        }
        Finished?.Invoke(job);
    }

    /// <summary>Понятное человеку сообщение вместо технического.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        ImageMagick.MagickException => $"Не удалось обработать изображение: {FirstLine(ex.Message)}",
        UnauthorizedAccessException => "Нет доступа к файлу или папке.",
        FileNotFoundException or DirectoryNotFoundException => "Файл не найден — возможно, его переместили.",
        IOException io when io.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) => "Файл занят другой программой.",
        _ => FirstLine(ex.Message),
    };

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    /// <summary>Передаёт прогресс в UI-поток не чаще, чем нужно.</summary>
    private sealed class Reporter(Dispatcher dispatcher, Job job) : IProgress<double>
    {
        private double _last = -1;

        public void Report(double value)
        {
            if (Math.Abs(value - _last) < 0.01 && value < 1)
                return;
            _last = value;
            dispatcher.BeginInvoke(() => job.Progress = value);
        }
    }
}
