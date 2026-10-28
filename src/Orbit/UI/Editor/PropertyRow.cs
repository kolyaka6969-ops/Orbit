using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Orbit.Core;

namespace Orbit.UI.Editor;

/// <summary>
/// Свойство клипа в панели справа: название, кнопки ключей (предыдущий, поставить или убрать, следующий) и ползунки
/// с полем для числа. Значения — в единицах панели (проценты, пиксели, градусы).
/// </summary>
internal sealed class PropertyRow : StackPanel
{
    public sealed record Axis(string Label, double Min, double Max, string Unit);

    private readonly int _lane;
    private readonly Slider[] _sliders;
    private readonly TextBox[] _boxes;
    private readonly Axis[] _axes;
    private readonly TextBlock _diamond;
    private readonly Button _previous;
    private readonly Button _next;
    private bool _updating;

    public PropertyRow(int lane, string title, params Axis[] axes)
    {
        _lane = lane;
        _axes = axes;
        Margin = new Thickness(0, 0, 0, 10);

        var head = new DockPanel { LastChildFill = true };
        var keys = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(keys, Dock.Right);
        _previous = Small("", "Предыдущий ключ", () => KeyAction?.Invoke(_lane, KeyCommand.Previous));
        _diamond = new TextBlock { Text = "◇", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center };
        var toggle = Small("", "Поставить или убрать ключ на бегунке", () => KeyAction?.Invoke(_lane, KeyCommand.Toggle));
        toggle.Content = _diamond;
        _next = Small("", "Следующий ключ", () => KeyAction?.Invoke(_lane, KeyCommand.Next));
        keys.Children.Add(_previous);
        keys.Children.Add(toggle);
        keys.Children.Add(_next);
        head.Children.Add(keys);
        head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Children.Add(head);

        _sliders = new Slider[axes.Length];
        _boxes = new TextBox[axes.Length];
        for (var i = 0; i < axes.Length; i++)
        {
            var index = i;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
            if (axes[i].Label.Length > 0)
            {
                var label = new TextBlock { Text = axes[i].Label, Width = 16, VerticalAlignment = VerticalAlignment.Center };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                DockPanel.SetDock(label, Dock.Left);
                row.Children.Add(label);
            }
            var box = new TextBox { Width = 74, Margin = new Thickness(8, 0, 0, 0), TextAlignment = TextAlignment.Right, FontSize = 13, MinHeight = 30, Padding = new Thickness(6, 3, 6, 3) };
            DockPanel.SetDock(box, Dock.Right);
            box.KeyDown += (_, e) =>
            {
                // Дальше снова работают пробел, S, K и другие клавиши редактора
                if (e.Key == Key.Enter)
                {
                    Commit(index);
                    Done?.Invoke();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    _boxes[index].Text = Format(index, _sliders[index].Value);
                    Done?.Invoke();
                    e.Handled = true;
                }
            };
            box.LostKeyboardFocus += (_, _) => Commit(index);
            row.Children.Add(box);
            var slider = new Slider { Minimum = axes[i].Min, Maximum = axes[i].Max, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
            slider.ValueChanged += (_, _) =>
            {
                if (_updating)
                    return;
                _boxes[index].Text = Format(index, slider.Value);
                Changed?.Invoke(_lane, _sliders.Select(s => s.Value).ToArray());
            };
            slider.PreviewMouseLeftButtonDown += (_, _) => DragStarted?.Invoke(_lane);
            row.Children.Add(slider);
            _sliders[i] = slider;
            _boxes[i] = box;
            Children.Add(row);
        }
    }

    public event Action<int, KeyCommand>? KeyAction;

    /// <summary>Новое значение (по осям) — от ползунка или поля.</summary>
    public event Action<int, double[]>? Changed;

    /// <summary>Ввод в поле закончен (Enter или Esc) — фокус пора вернуть редактору.</summary>
    public event Action? Done;

    /// <summary>Начали тянуть ползунок — одна правка для отмены.</summary>
    public event Action<int>? DragStarted;

    private static Button Small(string glyph, string tip, Action click)
    {
        var button = new Button { Content = glyph, ToolTip = tip, Width = 28, Height = 28, FontSize = 11 };
        button.SetResourceReference(StyleProperty, "IconButton");
        button.Click += (_, _) => click();
        return button;
    }

    private string Format(int axis, double value) => value.ToString(Math.Abs(value) < 10 && _axes[axis].Unit == "°" ? "0.#" : "0", CultureInfo.CurrentCulture) + _axes[axis].Unit;

    private void Commit(int axis)
    {
        var text = _boxes[axis].Text.Replace(_axes[axis].Unit, "").Replace(',', '.').Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            _boxes[axis].Text = Format(axis, _sliders[axis].Value);
            return;
        }
        if (Math.Abs(value - _sliders[axis].Value) < 1e-9)
            return;
        DragStarted?.Invoke(_lane);
        var values = _sliders.Select(s => s.Value).ToArray();
        // Поле принимает и значения за пределами ползунка (например, масштаб 800 %)
        values[axis] = value;
        Changed?.Invoke(_lane, values);
    }

    /// <summary>Показать значения в месте бегунка и состояние ключей.</summary>
    public void Show(double[] values, Keyframe? keyHere, bool animated)
    {
        _updating = true;
        for (var i = 0; i < _sliders.Length; i++)
        {
            _sliders[i].Value = Math.Clamp(values[i], _sliders[i].Minimum, _sliders[i].Maximum);
            if (!_boxes[i].IsKeyboardFocused)
                _boxes[i].Text = Format(i, values[i]);
        }
        _updating = false;
        _diamond.Text = keyHere is null ? "◇" : "◆";
        if (keyHere is not null || animated)
            _diamond.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        else
            _diamond.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        _previous.IsEnabled = _next.IsEnabled = animated;
    }
}
